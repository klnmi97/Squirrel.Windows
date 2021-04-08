using Squirrel.SimpleSplat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Squirrel
{
    /// <summary>
    /// Download manager for parallel download of parts of a file with
    /// connection error handling. Expects that server has partial
    /// content support. 
    /// Inspired by <a href="https://dejanstojanovic.net/aspnet/2018/march/download-file-in-chunks-in-parallel-in-c/">tutorial</a>.
    /// </summary>
    public sealed class DownloadManager : IEnableLogger
    {
        /// <summary>Singleton's lock.</summary>
        private static readonly object instanceLock = new object();

        /// <summary>Singleton.</summary>
        private static DownloadManager instance = null;

        /// <summary>Last error which occured while downloading parts in parallel.</summary>
        private static volatile DownloadResult lastError = DownloadResult.OK;

        /// <summary>Below this limit (in bytes) is not possible to download file in parallel.</summary>
        private const long parallelDownloadLimit = 100 * 1024 * 1024;

        /// <summary>Buffer size for asynchronous reading of stream from the body of http response.</summary>
        private const long bufferSize = 1 * 1024 * 1024;

        /// <summary>Timeout (in milliseconds) for the body of http response reading.</summary>
        private const int streamReadTimeout = 5 * 60 * 1000;

        /// <summary>To be able to cancel file download.</summary>
        private CancellationTokenSource downloadFileTokenSource;

        /// <summary>Enumeration with possible parallel download results.</summary>
        enum DownloadResult
        {
            OK,
            INTERNET_CONNECTION_LOST,
            CONNECTION_EXCEPTION,
            NOT_ENOUGH_SPACE
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

        /// <summary>Holds info about downloaded file part.</summary>
        private class FilePartInfo
        {
            /// <summary>Path to the temporary file for the downloaded part.</summary>
            public string FilePath { get; set; }
            /// <summary>Current progress of the downloaded part.</summary>
            public int Progress { get; set; }
            /// <summary>Flag which indicates if the part has been successfully downloaded.</summary>
            public bool Finished { get; set; }
        }

        /// <summary>Singleton property.</summary>
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

        /// <summary>Check the internet connection availability against this url.</summary>
        public string NetCheckUrl { get; set; } = "http://google.com/generate_204";

        DownloadManager()
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.MaxServicePointIdleTime = 1000;
        }

        /// <summary>
        /// Download file in parallel with several attempts.
        /// </summary>
        /// <param name="fileUrl">File url.</param>
        /// <param name="destinationFilePath">Path to save downloaded file.</param>
        /// <param name="numberOfParallelDownloads">If set to zero <see cref="Environment.ProcessorCount"/> will be used.</param>
        /// <param name="progress">Ui method for showing progress.</param>
        /// <param name="validateSSL">Whether validate SSL.</param>
        public void DownloadFile(string fileUrl, string destinationFilePath, int numberOfParallelDownloads, Action<int> progress, bool validateSSL = false)
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
                ThrowException(netCheckerTokenSource);
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

            // Attempts to download the file.
            for (int i = 0; i < 3; i++)
            {
                if (CheckForInternetConnection())
                {
                    if (downloadExitCode = ParallelDownload(fileUrl, fileSize, destinationFilePath, filePartsInfo, progress, parallelDownloads))
                    {
                        break;
                    }
                }

                // It does not make any sense to do next attempt in the case of these errors.
                if (lastError == DownloadResult.CONNECTION_EXCEPTION || lastError == DownloadResult.NOT_ENOUGH_SPACE)
                {
                    break;
                }

                System.Threading.Thread.Sleep(30000);
                downloadFileTokenSource = new CancellationTokenSource();
            }

            lock (filePartsInfo.SyncRoot)
            {
                // Delete temporary file parts.
                foreach (var filePart in filePartsInfo)
                {
                    File.Delete(filePart.FilePath);
                }
            }

            if (!downloadExitCode)
            {
                ThrowException(netCheckerTokenSource);
            }

            netCheckerTokenSource.Cancel();
        }

        /// <summary>
        /// Create net checker task which checks internet connection availability against <cref="NetCheckUrl"/>.
        /// </summary>
        /// <returns>Created task.</returns>
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

        /// <summary>
        /// Get downloaded file size.
        /// </summary>
        /// <param name="fileUrl">Url of the file.</param>
        /// <returns>File size, zero if fails.</returns>
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
                    lastError = DownloadResult.CONNECTION_EXCEPTION;
                    this.Log().WarnException(String.Format("Couldn't get HEAD with file size."), ex);
                    System.Threading.Thread.Sleep(5000);
                }
            }

            return responseLength;
        }

        /// <summary>
        /// Downloads file in parallel.
        /// </summary>
        /// <param name="fileUrl">Url of the file.</param>
        /// <param name="fileSize">Size of the file.</param>
        /// <param name="destinationFilePath">Path to save downloaded file.</param>
        /// <param name="filePartsInfo">Array with file part metadata.</param>
        /// <param name="progress">Ui method for showing progress.</param>
        /// <param name="numberOfParallelDownloads">Number of parallel downloads.</param>
        /// <returns>True if succeeds, otherwise false.</returns>
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
                    // Check all chunks if they were downloaded successfully.
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

        /// <summary>
        /// Downloads specified byte range of the file.
        /// </summary>
        /// <param name="range">Range to download.</param>
        /// <param name="index">Index in the parallel for loop.</param>
        /// <param name="state">State of the parallel for loop.</param>
        /// <param name="fileUrl">Url of the file.</param>
        /// <param name="filePartsInfo">Array with file part metadata.</param>
        /// <param name="progress">Ui method for showing progress.</param>
        private void RangeDownload(Range range, int index, ParallelLoopState state, string fileUrl,
            FilePartInfo[] filePartsInfo, Action<int> progress)
        {
            string tempFilePath = "";

            // Attempts to download file part.
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
                            // Temporary file for the downloaded part does not exist, create a new one.
                            tempFilePath = Path.GetTempFileName();
                            filePartsInfo[index].FilePath = tempFilePath;
                        }
                    }

                    httpWebRequest.AddRange(range.Start, range.End);

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
                                // ReadAsync function can hangs in the case of internet connection problems because timeout for the stream does
                                // not work. The workaround is to substitute this timeout with Task.Delay.
                                Task<int> readTask = httpWebResponse.GetResponseStream().ReadAsync(buffer, 0, buffer.Length);

                                if (Task.WaitAny(new Task[] { readTask, Task.Delay(streamReadTimeout) }, downloadFileTokenSource.Token) == 1)
                                {
                                    lastError = DownloadResult.INTERNET_CONNECTION_LOST;
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
                                    filePartsInfo[index].Progress = (int)((bytesCounter * 100) / (range.End - range.Start));
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
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x27 || (ex.HResult & 0xFFFF) == 0x70)
                {
                    // 0x27 (ERROR_HANDLE_DISK_FULL) and 0x70 (ERROR_DISK_FULL) are standard Win32 error codes.
                    // See doc: https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/18d8fbe8-a967-4f1c-ae50-99ca8e491d2d
                    this.Log().WarnException(String.Format("Couldn't download file chunk number {0}. Not enough space.", index), ex);
                    lastError = DownloadResult.NOT_ENOUGH_SPACE;
                    state.Stop();
                    return;
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

        /// <summary>
        /// Checks if the internet connection is alive.
        /// </summary>
        /// <returns>True if succeeds, otherwise false.</returns>
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
                lastError = DownloadResult.INTERNET_CONNECTION_LOST;
                return false;
            }
        }

        /// <summary>
        /// Updates progress of the downloaded file.
        /// </summary>
        /// <param name="progress">Ui method for showing progress.</param>
        /// <param name="filePartsInfo">Array with file part metadata.</param>
        private void UpdateProgress(Action<int> progress, FilePartInfo[] filePartsInfo)
        {
            lock (filePartsInfo.SyncRoot)
            {
                progress(filePartsInfo.Sum(x => x.Progress) / filePartsInfo.Length);
            }
        }

        /// <summary>
        /// Throws exception based on the last error.
        /// </summary>
        private void ThrowException(CancellationTokenSource netCheckerTokenSource)
        {
            netCheckerTokenSource.Cancel();

            if (lastError == DownloadResult.CONNECTION_EXCEPTION || lastError == DownloadResult.INTERNET_CONNECTION_LOST)
            {
                throw new Exception("Connection exception.");
            }
            else if (lastError == DownloadResult.NOT_ENOUGH_SPACE)
            {
                // Throw error with code ERROR_HANDLE_DISK_FULL.
                throw new ExternalException("Not enough space.", unchecked((int)0x80070027));
            }
            else if (lastError == DownloadResult.OK)
            {
                this.Log().Warn("We should not get here. Download failed but last error is ok.");
                throw new InvalidOperationException();
            }
            else
            {
                // Some DownloadResult implementation is missing.
                throw new NotImplementedException();
            }
        }
    }
}
