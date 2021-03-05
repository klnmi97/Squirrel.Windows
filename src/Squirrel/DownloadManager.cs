using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Squirrel
{
	public sealed class DownloadManager
	{
		private static readonly object instanceLock = new object();
		private static DownloadManager instance = null;
		private static DownloadResult lastError = DownloadResult.OK;

		private const long parallelDownloadLimit = 100 * 1024 * 1024;

		static bool test = false;		

		enum DownloadResult
		{
			OK,
			INTERNET_LOST,
			CONNECTION_EXCEPTION
		}

		private class Range
		{
			public long Start { get; set; }
			public long End { get; set; }
		}

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

		DownloadManager()
		{
			ServicePointManager.Expect100Continue = false;
			ServicePointManager.DefaultConnectionLimit = 100;
			ServicePointManager.MaxServicePointIdleTime = 1000;
		}

		public bool DownloadFile(string fileUrl, string destinationFilePath, int numberOfParallelDownloads, bool validateSSL = false)
		{
			CancellationTokenSource downloadFileTokenSource = new CancellationTokenSource();
			CancellationTokenSource netCheckerTokenSource = new CancellationTokenSource();
			bool downloadExitCode = false;
			ConcurrentDictionary<int, Tuple<string, bool>> tempFilesDictionary = new ConcurrentDictionary<int, Tuple<string, bool>>();
			// Calculate destination path.
			long fileSize = 0;
			int parallelDownloads = numberOfParallelDownloads;

			lastError = DownloadResult.OK;

			if (!validateSSL)
			{
				ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
			}

			Task netCheckerTask = Task.Factory.StartNew(o =>
			{
				while (true)
				{
					if (netCheckerTokenSource.Token.IsCancellationRequested)
					{
						break;
					}

					if (!CheckForInternetConnection())
					{
						downloadFileTokenSource.Cancel();
					}

					Thread.Sleep(5000);
				}
			}, TaskCreationOptions.LongRunning, netCheckerTokenSource.Token);

			//DownloadResult result = new DownloadResult() { FilePath = destinationFilePath };

			if ((fileSize = GetDownloadedFileSize(fileUrl)) == 0)
			{
				return false;
			}

			//Handle number of parallel downloads  
			if (fileSize < parallelDownloadLimit)
			{
				parallelDownloads = 1;
			}
			else if (numberOfParallelDownloads <= 0)
			{
				parallelDownloads = Environment.ProcessorCount;
			}

			for (int i = 0; i < 3; i++)
			{
				if (downloadExitCode = ParallelDownload(fileUrl, fileSize, destinationFilePath, downloadFileTokenSource, tempFilesDictionary, parallelDownloads))
				{
					break;
				}

				if (lastError == DownloadResult.CONNECTION_EXCEPTION)
				{
					break;
				}

				System.Threading.Thread.Sleep(10000);
				downloadFileTokenSource = new CancellationTokenSource();
			}

			foreach (var tempFile in tempFilesDictionary)
			{
				File.Delete(tempFile.Value.Item1);
			}

			netCheckerTokenSource.Cancel();

			if (!downloadExitCode)
			{
				return false;
			}

			return true;
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
					using (HttpWebResponse httpWebResponse = httpWebRequest.GetResponse() as HttpWebResponse)
					{
						if (httpWebResponse.StatusCode != HttpStatusCode.OK)
						{
							System.Threading.Thread.Sleep(5000);
							continue;
						}

						// TODO: check for partial content support
						responseLength = long.Parse(httpWebResponse.Headers.Get("Content-Length"));
						//result.Size = responseLength;
						break;
					}
				}
				catch (WebException)
				{
					Console.WriteLine("WebException catched");
					System.Threading.Thread.Sleep(5000);
				}
				catch (Exception)
				{
					Console.WriteLine("Exception catched");
					System.Threading.Thread.Sleep(5000);
				}
			}

			return responseLength;
		}

		private bool ParallelDownload(string fileUrl, long fileSize, string destinationFilePath, CancellationTokenSource cancellationTokenSource, ConcurrentDictionary<int, Tuple<string, bool>> tempFilesDictionary, int numberOfParallelDownloads = 0)
		{
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

				//DateTime startTime = DateTime.Now;

				#region Parallel download  

				Parallel.For(0, numberOfParallelDownloads, new ParallelOptions() { MaxDegreeOfParallelism = numberOfParallelDownloads }, (i, state) =>
					RangeDownload(readRanges[i], i, state, cancellationTokenSource, fileUrl, tempFilesDictionary)
				);

				if (cancellationTokenSource.IsCancellationRequested)
				{
					return false;
				}

				//result.ParallelDownloads = index;

				#endregion

				//result.TimeTaken = DateTime.Now.Subtract(startTime);

				// Check all chunks if they were downloaded successfuly.
				foreach (var tempFile in tempFilesDictionary)
				{
					if (!tempFile.Value.Item2)
					{
						return false;
					}
				}

				#region Merge to single file 

				foreach (var tempFile in tempFilesDictionary.OrderBy(b => b.Key))
				{
					byte[] tempFileBytes = File.ReadAllBytes(tempFile.Value.Item1);
					destinationStream.Write(tempFileBytes, 0, tempFileBytes.Length);
					File.Delete(tempFile.Value.Item1);
				}

				#endregion

				return true;
			}
		}

		private void RangeDownload(Range readRange, int index, ParallelLoopState state, CancellationTokenSource cancellationTokenSource, string fileUrl, ConcurrentDictionary<int, Tuple<string, bool>> tempFilesDictionary)
		{
			string tempFilePath = "";

			for (int i = 0; i < 3; i++)
			{
				try
				{
					HttpWebRequest httpWebRequest = HttpWebRequest.Create(fileUrl) as HttpWebRequest;
					httpWebRequest.Method = "GET";

					//httpWebRequest.Timeout = 5*1000;
					//bool test = httpWebRequest.KeepAlive;

					Tuple<string, bool> tempFileTuple;

					if (tempFilesDictionary.TryGetValue(index, out tempFileTuple))
					{
						if (tempFileTuple.Item2)
						{
							return;
						}
					}
					else
					{
						tempFilePath = Path.GetTempFileName();
						tempFilesDictionary.TryAdd(index, new Tuple<string, bool>(tempFilePath, false));
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
							//httpWebResponse.GetResponseStream().ReadTimeout = 5000;
							Task copyTask = httpWebResponse.GetResponseStream().CopyToAsync(fileStream);

							/*if (index == 0)
							{
								test = true;
								System.Threading.Thread.Sleep(5000);
								throw new WebException();
							}*/

							copyTask.Wait(cancellationTokenSource.Token);							
							tempFilesDictionary[index] = new Tuple<string, bool>(tempFilePath, true);
						}
					}
				}
				catch (WebException)
				{
					//if (e.Status == WebExceptionStatus.Timeout || e.Status == WebExceptionStatus.KeepAliveFailure)
					Console.WriteLine("WebException catched");
					//System.Threading.Thread.Sleep(5000);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception)
				{
					Console.WriteLine("Exception catched");
					System.Threading.Thread.Sleep(5000);
				}
			}

			// This chunk cannot be downloaded, discard the whole file downloading.
			cancellationTokenSource.Cancel();

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
				using (client.OpenRead("http://google.com/generate_204"))
					return true;
			}
			catch
			{
				lastError = DownloadResult.INTERNET_LOST;
				return false;
			}
		}
	}
}
