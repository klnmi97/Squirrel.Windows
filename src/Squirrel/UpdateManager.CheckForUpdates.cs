﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Squirrel.SimpleSplat;

namespace Squirrel
{
    public sealed partial class UpdateManager
    {
        internal class CheckForUpdateImpl : IEnableLogger
        {
            readonly string rootAppDirectory;

            public CheckForUpdateImpl(string rootAppDirectory)
            {
                this.rootAppDirectory = rootAppDirectory;
            }

            public async Task<UpdateInfo> CheckForUpdate(
                UpdaterIntention intention,
                string localReleaseFile,
                string updateUrlOrPath,
                string token,
                int maxDeltas,
                string releasesFileName,
                bool ignoreDeltaUpdates = false,
                Action<int> progress = null,
                Action<string> status = null)
            {
                progress = progress ?? (_ => { });
                status = status ?? (_ => { });

                status("Checking for updates");

                var localReleases = Enumerable.Empty<ReleaseEntry>();
                var stagingId = intention == UpdaterIntention.Install ? null : getOrCreateStagedUserId();

                bool shouldInitialize = intention == UpdaterIntention.Install;

                if (intention != UpdaterIntention.Install) {
                    try {
                        localReleases = Utility.LoadLocalReleases(localReleaseFile);
                    }
                    catch (Exception ex) {
                        // Something has gone pear-shaped, let's start from scratch
                        this.Log().WarnException("Failed to load local releases, starting from scratch", ex);
                        shouldInitialize = true;
                    }
                }

                if (shouldInitialize) await initializeClientAppDirectory();

                string releaseFile;

                var latestLocalRelease = localReleases.Count() > 0 ?
                    localReleases.MaxBy(x => x.Version).First() :
                    default(ReleaseEntry);

                // Fetch the remote RELEASES file, whether it's a local dir or an
                // HTTP URL
                if (Utility.IsHttpUrl(updateUrlOrPath)) {
                    if (updateUrlOrPath.EndsWith("/")) {
                        updateUrlOrPath = updateUrlOrPath.Substring(0, updateUrlOrPath.Length - 1);
                    }

                    this.Log().Info("Downloading {0} file from {1}", releasesFileName, updateUrlOrPath);
                    
                    var uri = Utility.AppendPathToUri(new Uri(updateUrlOrPath), releasesFileName);

                    // PO: The update server does not expect these params, so we do not want to add these params
                    //     when calling our update server. We need to add authentication SAS token only.
                    /*if (latestLocalRelease != null) {
                        uri = Utility.AddQueryParamsToUri(uri, new Dictionary<string, string> {
                            { "id", latestLocalRelease.PackageName },
                            { "localVersion", latestLocalRelease.Version.ToString() },
                            { "arch", Environment.Is64BitOperatingSystem ? "amd64" : "x86" }
                        });
                    }*/

                    string tempFilePath = Path.GetTempFileName();

                    try
                    {
                        DownloadManager.Instance.DownloadFile(uri.ToString() + token, tempFilePath, 1, progress, x => { });
                    }
                    catch(Exception ex)
                    {
                        File.Delete(tempFilePath);
                        status("Checking for updates failed");
                        throw ex;
                    }

                    var data = File.ReadAllBytes(tempFilePath);
                    File.Delete(tempFilePath);

                    releaseFile = Encoding.UTF8.GetString(data);
                    progress(33);
                } else {
                    this.Log().Info("Reading {0} file from {1}", releasesFileName, updateUrlOrPath);

                    if (!Directory.Exists(updateUrlOrPath)) {
                        var message = String.Format(
                            "The directory {0} does not exist, something is probably broken with your application",
                            updateUrlOrPath);

                        status("Checking for updates failed");
                        throw new Exception(message);
                    }

                    var fi = new FileInfo(Path.Combine(updateUrlOrPath, releasesFileName));
                    if (!fi.Exists) {
                        var message = String.Format(
                            "The file {0} does not exist, something is probably broken with your application",
                            fi.FullName);

                        this.Log().Warn(message);

                        var packages = (new DirectoryInfo(updateUrlOrPath)).GetFiles("*.nupkg");
                        if (packages.Length == 0) {
                            throw new Exception(message);
                        }

                        // NB: Create a new RELEASES file since we've got a directory of packages
                        ReleaseEntry.WriteReleaseFile(
                            packages.Select(x => ReleaseEntry.GenerateFromFile(x.FullName)), fi.FullName);
                    }

                    releaseFile = File.ReadAllText(fi.FullName, Encoding.UTF8);
                    progress(33);
                }

                var ret = default(UpdateInfo);
                var remoteReleases = ReleaseEntry.ParseReleaseFileAndApplyStaging(releaseFile, stagingId);
                progress(66);

                if (!remoteReleases.Any()) {
                    status("Remote release File is empty or corrupted");
                    throw new Exception("Remote release File is empty or corrupted");
                }

                ret = determineUpdateInfo(intention, localReleases, remoteReleases, ignoreDeltaUpdates, maxDeltas);

                progress(100);
                return ret;
            }

            async Task initializeClientAppDirectory()
            {
                // On bootstrap, we won't have any of our directories, create them
                var pkgDir = Path.Combine(rootAppDirectory, "packages");
                if (Directory.Exists(pkgDir)) {
                    await Utility.DeleteDirectory(pkgDir);
                }

                Directory.CreateDirectory(pkgDir);
            }

            UpdateInfo determineUpdateInfo(UpdaterIntention intention, IEnumerable<ReleaseEntry> localReleases, IEnumerable<ReleaseEntry> remoteReleases, bool ignoreDeltaUpdates, int maxDeltas)
            {
                var packageDirectory = Utility.PackageDirectoryForAppDir(rootAppDirectory);
                localReleases = localReleases ?? Enumerable.Empty<ReleaseEntry>();

                if (remoteReleases == null) {
                    this.Log().Warn("Release information couldn't be determined due to remote corrupt RELEASES file");
                    throw new Exception("Corrupt remote RELEASES file");
                }

                var latestFullRelease = Utility.FindCurrentVersion(remoteReleases);
                var currentRelease = Utility.FindCurrentVersion(localReleases);

                if (latestFullRelease == currentRelease) {
                    this.Log().Info("No updates, remote and local are the same");

                    var info = UpdateInfo.Create(currentRelease, new[] {latestFullRelease}, packageDirectory, maxDeltas);
                    return info;
                }

                if (ignoreDeltaUpdates) {
                    remoteReleases = remoteReleases.Where(x => !x.IsDelta);
                }

                if (!localReleases.Any()) {
                    if (intention == UpdaterIntention.Install) {
                        this.Log().Info("First run, starting from scratch");
                    } else {
                        this.Log().Warn("No local releases found, starting from scratch");
                    }

                    return UpdateInfo.Create(null, new[] {latestFullRelease}, packageDirectory, maxDeltas);
                }

                if (localReleases.Max(x => x.Version) > remoteReleases.Max(x => x.Version)) {
                    this.Log().Warn("hwhat, local version is greater than remote version");
                    return UpdateInfo.Create(Utility.FindCurrentVersion(localReleases), new[] {latestFullRelease}, packageDirectory, maxDeltas);
                }

                return UpdateInfo.Create(currentRelease, remoteReleases, packageDirectory, maxDeltas);
            }

            internal Guid? getOrCreateStagedUserId()
            {
                var stagedUserIdFile = Path.Combine(rootAppDirectory, "packages", ".betaId");
                var ret = default(Guid);

                try {
                    if (!Guid.TryParse(File.ReadAllText(stagedUserIdFile, Encoding.UTF8), out ret)) {
                        throw new Exception("File was read but contents were invalid");
                    }

                    this.Log().Info("Using existing staging user ID: {0}", ret.ToString());
                    return ret;
                } catch (Exception ex) {
                    this.Log().DebugException("Couldn't read staging user ID, creating a blank one", ex);
                }

                var prng = new Random();
                var buf = new byte[4096];
                prng.NextBytes(buf);

                ret = Utility.CreateGuidFromHash(buf);
                try {
                    File.WriteAllText(stagedUserIdFile, ret.ToString(), Encoding.UTF8);
                    this.Log().Info("Generated new staging user ID: {0}", ret.ToString());
                    return ret;
                } catch (Exception ex) {
                    this.Log().WarnException("Couldn't write out staging user ID, this user probably shouldn't get beta anything", ex);
                    return null;
                }
            }
        }
    }
}
