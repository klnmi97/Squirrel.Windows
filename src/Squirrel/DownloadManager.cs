using Squirrel.SimpleSplat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Squirrel
{
	public sealed class DownloadManager : IEnableLogger
	{
		/// <summary>Singleton's lock.</summary>
		private static readonly object instanceLock = new object();

		/// <summary>Singleton.</summary>
		private static DownloadManager instance = null;

		/// <summary>Last error which occured during parallel parts download.</summary>
		private static volatile DownloadResult lastError = DownloadResult.OK;

		/// <summary>Below this limit (in bytes) is not possible to download file in parallel.</summary>
		private const long parallelDownloadLimit = 100 * 1024 * 1024;

		/// <summary>Buffer size for asynchronous reading of stream from the body of http response.</summary>
		private const long bufferSize = 1 * 1024 * 1024;

		/// <summary>Timeout (in milliseconds) for the body of http response reading.</summary>
		private const int streamReadTimeout = 5 * 60 * 1000;

		private CancellationTokenSource downloadFileTokenSource;

		/// <summary> Enumeration with possible parallel download results.</summary>
		enum DownloadResult
		{
			OK,
			INTERNET_LOST,
			CONNECTION_EXCEPTION
		}

		/// <summary>
		/// Download range of the file part.
		/// TODO: PO - could be in the FilePartInfo
		/// </summary>
		private class Range
		{
			public long Start { get; set; }
			public long End { get; set; }
		}

		/// <summary> Holds info about downloaded file part. </summary>
		private class FilePartInfo
		{
			/// <summary> Path to the temporary file for the downloaded part. </summary>
			public string FilePath { get; set; }
			/// <summary> Current progress of the downloaded part. </summary>
			public int Progress { get; set; }
			/// <summary> Flag which indicates if the part is successfully downloaded. </summary>
			public bool Finished { get; set; }
		}

		/// <summary> Singleton property. </summary>
		public static DownloadManager Instance
		{
			get
			{
				lock (instanceLock)
				{
					if (instance == null)
					{
						instance = new DownloadManager();
					}
					return instance;
				}
			}
		}

		public string NetCheckUrl { get; set; } = "http://google.com/generate_204";

		DownloadManager()
		{
			ServicePointManager.Expect100Continue = false;
			ServicePointManager.DefaultConnectionLimit = 100;
			ServicePointManager.MaxServicePointIdleTime = 1000;
		}

		public bool DownloadFile(string fileUrl, string destinationFilePath, int numberOfParallelDownloads, Action<int> progress, bool validateSSL = false)
		{
			CancellationTokenSource netCheckerTokenSource = new CancellationTokenSource();
			downloadFileTokenSource = new CancellationTokenSource();
			bool downloadExitCode = false;
			FilePartInfo[] filePartsInfo;
			long fileSize = 0;
			int parallelDownloads = numberOfParallelDownloads;

			if (!validateSSL)
			{
				ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
			}

			Task netCheckerTask = CreateNetCheckerTask(netCheckerTokenSource);

			if ((fileSize = GetDownloadedFileSize(fileUrl)) == 0)
			{
				return false;
			}

			// Handle number of parallel downloads.
			if (fileSize < parallelDownloadLimit)
			{
				parallelDownloads = 1;
			}
			else if (numberOfParallelDownloads <= 0)
			{
				parallelDownloads = Environment.ProcessorCount;
			}

			filePartsInfo = new FilePartInfo[parallelDownloads];

			for (int i = 0; i < parallelDownloads; i++)
			{
				filePartsInfo[i] = new FilePartInfo();
			}

			for (int i = 0; i < 3; i++)
			{
				if (CheckForInternetConnection())
				{
					if (downloadExitCode = ParallelDownload(fileUrl, fileSize, destinationFilePath, filePartsInfo, progress, parallelDownloads))
					{
						break;
					}
				}

				if (lastError == DownloadResult.CONNECTION_EXCEPTION)
				{
					break;
				}

				System.Threading.Thread.Sleep(30000);
				downloadFileTokenSource = new CancellationTokenSource();
			}

			lock (filePartsInfo.SyncRoot)
			{
				foreach (var filePart in filePartsInfo)
				{
					File.Delete(filePart.FilePath);
				}
			}

			netCheckerTokenSource.Cancel();

			if (!downloadExitCode)
			{
				return false;
			}

			return true;
		}

		private Task CreateNetCheckerTask(CancellationTokenSource netCheckerTokenSource)
		{
			return Task.Factory.StartNew(o =>
			{
				int counter = 0;

				while (true)
				{
					if (netCheckerTokenSource.Token.IsCancellationRequested)
					{
						break;
					}

					if (CheckForInternetConnection())
					{
						if (counter >= 6)
						{
							this.Log().Info(String.Format("Internet connection is back alive."));
						}

						counter = 0;
					}
					else
					{
						counter++;
					}

					// At least 5 seconds without internet connection.
					if (counter >= 6)
					{
						this.Log().Info(String.Format("Internet connection lost."));
						downloadFileTokenSource.Cancel();
					}

					Thread.Sleep(1000);
				}
			}, TaskCreationOptions.LongRunning, netCheckerTokenSource.Token);
		}

		private long GetDownloadedFileSize(string fileUrl)
		{
			long responseLength = 0;

			for (int i = 0; i < 3; i++)
			{
				try
				{
					HttpWebRequest httpWebRequest = HttpWebRequest.Create(fileUrl) as HttpWebRequest;
					httpWebRequest.Method = "HEAD";
					httpWebRequest.Proxy = WebRequest.DefaultWebProxy;
					using (HttpWebResponse httpWebResponse = httpWebRequest.GetResponse() as HttpWebResponse)
					{
						if (httpWebResponse.StatusCode != HttpStatusCode.OK)
						{
							System.Threading.Thread.Sleep(5000);
							continue;
						}

						// TODO: check for partial content support
						responseLength = long.Parse(httpWebResponse.Headers.Get("Content-Length"));
						break;
					}
				}
				catch (Exception ex)
				{
					this.Log().WarnException(String.Format("Couldn't get HEAD with file size."), ex);
					System.Threading.Thread.Sleep(5000);
				}
			}

			return responseLength;
		}

		private bool ParallelDownload(string fileUrl, long fileSize, string destinationFilePath,
			FilePartInfo[] filePartsInfo, Action<int> progress, int numberOfParallelDownloads = 0)
		{
			lastError = DownloadResult.OK;

			if (File.Exists(destinationFilePath))
			{
				File.Delete(destinationFilePath);
			}

			using (FileStream destinationStream = new FileStream(destinationFilePath, FileMode.Append))
			{
				#region Calculate ranges

				List<Range> readRanges = new List<Range>();
				for (int chunk = 0; chunk < numberOfParallelDownloads - 1; chunk++)
				{
					var range = new Range()
					{
						Start = chunk * (fileSize / numberOfParallelDownloads),
						End = ((chunk + 1) * (fileSize / numberOfParallelDownloads)) - 1
					};
					readRanges.Add(range);
				}

				readRanges.Add(new Range()
				{
					Start = readRanges.Any() ? readRanges.Last().End + 1 : 0,
					End = fileSize - 1
				});

				#endregion

				#region Parallel download

				Parallel.For(0, numberOfParallelDownloads, new ParallelOptions() { MaxDegreeOfParallelism = numberOfParallelDownloads }, (i, state) =>
					RangeDownload(readRanges[i], i, state, fileUrl, filePartsInfo, progress)
				);

				if (downloadFileTokenSource.IsCancellationRequested)
				{
					return false;
				}

				#endregion

				lock (filePartsInfo.SyncRoot)
				{
					// Check all chunks if they were downloaded successfuly.
					foreach (var filePart in filePartsInfo)
					{
						if (!filePart.Finished)
						{
							return false;
						}
					}

					#region Merge to single file 

					foreach (var filePart in filePartsInfo)
					{
						byte[] filePartBytes = File.ReadAllBytes(filePart.FilePath);
						destinationStream.Write(filePartBytes, 0, filePartBytes.Length);
						File.Delete(filePart.FilePath);
					}

					#endregion
				}

				return true;
			}
		}

		private void RangeDownload(Range readRange, int index, ParallelLoopState state, string fileUrl,
			FilePartInfo[] filePartsInfo, Action<int> progress)
		{
			string tempFilePath = "";

			for (int i = 0; i < 3; i++)
			{
				try
				{
					HttpWebRequest httpWebRequest = HttpWebRequest.Create(fileUrl) as HttpWebRequest;
					httpWebRequest.Method = "GET";
					httpWebRequest.Proxy = WebRequest.DefaultWebProxy;

					lock (filePartsInfo.SyncRoot)
					{
						if (filePartsInfo[index].FilePath != null)
						{
							if (filePartsInfo[index].Finished)
							{
								return;
							}

							tempFilePath = filePartsInfo[index].FilePath;
						}
						else
						{
							tempFilePath = Path.GetTempFileName();
							filePartsInfo[index].FilePath = tempFilePath;
						}
					}

					httpWebRequest.AddRange(readRange.Start, readRange.End);

					using (HttpWebResponse httpWebResponse = httpWebRequest.GetResponse() as HttpWebResponse)
					{
						if (httpWebResponse.StatusCode != HttpStatusCode.PartialContent)
						{
							System.Threading.Thread.Sleep(5000);
							continue;
						}

						using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.Write))
						{
							byte[] buffer = new byte[bufferSize];
							long bytesCounter = 0;

							while (true)
							{
								Task<int> readTask = httpWebResponse.GetResponseStream().ReadAsync(buffer, 0, buffer.Length);

								if (Task.WaitAny(new Task[] { readTask, Task.Delay(streamReadTimeout) }, downloadFileTokenSource.Token) == 1)
								{
									lastError = DownloadResult.INTERNET_LOST;
									state.Stop();
									this.Log().Info(String.Format("Downloading of the chunk number {0} was cancelled.", index));
									break;
								}

								int currentBytes = readTask.Result;

								if (currentBytes == 0)
								{
									lock (filePartsInfo.SyncRoot)
									{
										filePartsInfo[index].Finished = true;
									}

									return;
								}

								fileStream.Write(buffer, 0, currentBytes);
								bytesCounter += currentBytes;

								lock (filePartsInfo.SyncRoot)
								{
									filePartsInfo[index].Progress = (int)((bytesCounter * 100) / (readRange.End - readRange.Start));
								}

								UpdateProgress(progress, filePartsInfo);

								if (state.IsStopped)
								{
									break;
								}

								if (downloadFileTokenSource.Token.IsCancellationRequested)
								{
									state.Stop();
									break;
								}
							}
						}
					}
				}
				catch (OperationCanceledException ex)
				{
					this.Log().WarnException(String.Format("Downloading of the chunk number {0} was cancelled.", index), ex);
				}
				catch (Exception ex)
				{
					this.Log().WarnException(String.Format("Couldn't download file chunk number {0}.", index), ex);
					System.Threading.Thread.Sleep(5000);
				}

				lock (filePartsInfo.SyncRoot)
				{
					filePartsInfo[index].Progress = 0;
				}

				if (state.IsStopped)
				{
					return;
				}

				if (downloadFileTokenSource.Token.IsCancellationRequested)
				{
					state.Stop();
					return;
				}
			}

			// This chunk cannot be downloaded, discard the whole file downloading.
			downloadFileTokenSource.Cancel();

			if (lastError == DownloadResult.OK)
			{
				lastError = DownloadResult.CONNECTION_EXCEPTION;
			}
		}

		private long GetExistingFileLength(string filename)
		{
			if (!File.Exists(filename)) return 0;
			FileInfo info = new FileInfo(filename);
			return info.Length;
		}

		private bool CheckForInternetConnection()
		{
			try
			{
				using (var client = new WebClient())
				{
					client.Proxy = WebRequest.DefaultWebProxy;
					using (client.OpenRead(NetCheckUrl))
						return true;
				}
			}
			catch
			{
				lastError = DownloadResult.INTERNET_LOST;
				return false;
			}
		}

		private void UpdateProgress(Action<int> progress, FilePartInfo[] filePartsInfo)
		{
			lock (filePartsInfo.SyncRoot)
			{
				progress(filePartsInfo.Sum(x => x.Progress) / filePartsInfo.Length);
			}
		}
	}
}
