﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Squirrel.SimpleSplat;

namespace Squirrel
{
    public sealed partial class UpdateManager
    {
        internal class DownloadReleasesImpl : IEnableLogger
        {
            readonly string rootAppDirectory;

            public DownloadReleasesImpl(string rootAppDirectory)
            {
                this.rootAppDirectory = rootAppDirectory;
            }

            public async Task DownloadReleases(string updateUrlOrPath, string token, IEnumerable<ReleaseEntry> releasesToDownload, int parallelDownloadLimit, 
                Action<int> progress = null, Action<string> status = null)
            {
                progress = progress ?? (_ => { });
                status = status ?? (_ => { });
                status("Downloading updates");

                var packagesDirectory = Path.Combine(rootAppDirectory, "packages");

                double current = 0;
                double toIncrement = 100.0 / releasesToDownload.Count();

                if (Utility.IsHttpUrl(updateUrlOrPath)) {
                    // From Internet
                    /*await releasesToDownload.ForEachAsync(async x => {
                        var targetFile = Path.Combine(packagesDirectory, x.Filename);
                        double component = 0;
                        await downloadRelease(updateUrlOrPath, x, urlDownloader, targetFile, p => {
                            lock (progress) {
                                current -= component;
                                component = toIncrement / 100.0 * p;
                                progress((int)Math.Round(current += component));
                            }
                        });

                        checksumPackage(x);
                    });*/

                    releasesToDownload.ForEach(x =>
                    {
                        var targetFile = Path.Combine(packagesDirectory, x.Filename);
                        double component = 0;
                        downloadRelease(updateUrlOrPath, token, x, targetFile, parallelDownloadLimit, p =>
                        {
                            lock (progress)
                            {
                                current -= component;
                                component = toIncrement / 100.0 * p;
                                progress((int)Math.Round(current += component));
                            }
                        }, status);
                        status("Checking downloaded packages");
                        checksumPackage(x);
                    });
                }
                else {
                    // From Disk
                    await releasesToDownload.ForEachAsync(x => {
                        status("Copying files...");
                        var targetFile = Path.Combine(packagesDirectory, x.Filename);

                        File.Copy(
                            Path.Combine(updateUrlOrPath, x.Filename),
                            targetFile,
                            true);

                        lock (progress) progress((int)Math.Round(current += toIncrement));
                        checksumPackage(x);
                    });
                }
            }

            bool isReleaseExplicitlyHttp(ReleaseEntry x)
            {
                return x.BaseUrl != null && 
                    Uri.IsWellFormedUriString(x.BaseUrl, UriKind.Absolute);
            }

            void downloadRelease(string updateBaseUrl, string token, ReleaseEntry releaseEntry, string targetFile, int parallelDownloadLimit, 
                Action<int> progress, Action<string> status)
            {
                var baseUri = Utility.EnsureTrailingSlash(new Uri(updateBaseUrl));

                var releaseEntryUrl = releaseEntry.BaseUrl + releaseEntry.Filename;
                if (!String.IsNullOrEmpty(releaseEntry.Query)) {
                    releaseEntryUrl += releaseEntry.Query + "&" + token.Substring(1);
                }
                else {
                    releaseEntryUrl += token;
                }

                var sourceFileUrl = new Uri(baseUri, releaseEntryUrl).AbsoluteUri;
                File.Delete(targetFile);

                DownloadManager.Instance.DownloadFile(sourceFileUrl, targetFile, parallelDownloadLimit, progress, status);
            }

            Task checksumAllPackages(IEnumerable<ReleaseEntry> releasesDownloaded)
            {
                return releasesDownloaded.ForEachAsync(x => checksumPackage(x));
            }

            void checksumPackage(ReleaseEntry downloadedRelease)
            {
                var targetPackage = new FileInfo(
                    Path.Combine(rootAppDirectory, "packages", downloadedRelease.Filename));

                if (!targetPackage.Exists) {
                    this.Log().Error("File {0} should exist but doesn't", targetPackage.FullName);

                    throw new Exception("Checksummed file doesn't exist: " + targetPackage.FullName);
                }

                if (targetPackage.Length != downloadedRelease.Filesize) {
                    this.Log().Error("File Length should be {0}, is {1}", downloadedRelease.Filesize, targetPackage.Length);
                    targetPackage.Delete();

                    throw new Exception("Checksummed file size doesn't match: " + targetPackage.FullName);
                }

                using (var file = targetPackage.OpenRead()) {
                    var hash = Utility.CalculateStreamSHA1(file);

                    if (!hash.Equals(downloadedRelease.SHA1,StringComparison.OrdinalIgnoreCase)) {
                        this.Log().Error("File SHA1 should be {0}, is {1}", downloadedRelease.SHA1, hash);
                        targetPackage.Delete();
                        throw new Exception("Checksum doesn't match: " + targetPackage.FullName);
                    }
                }
            }
        }
    }
}
