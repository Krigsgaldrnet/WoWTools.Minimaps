﻿using MPQToTACT.Helpers;
using MPQToTACT.MPQ;
using System.Threading.Tasks.Dataflow;

namespace MPQToTACT.Readers
{
    public class MPQReader
    {
        private const string LISTFILE_NAME = "(listfile)";

        public readonly Options Options;
        public readonly List<string> FileList;

        private readonly Queue<string> PatchArchives;
        private readonly string DataDirectory;

        public MPQReader(Options options, IList<string> patchArchives)
        {
            Options = options;
            FileList = new List<string>();

            PatchArchives = new Queue<string>(patchArchives);
            DataDirectory = Path.DirectorySeparatorChar + "DATA" + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Iterates all the data archives, extracting and BLT encoding all files
        /// <para>Patches are applied where applicable to get the most up-to-date variant of each file.</para>
        /// </summary>
        /// <param name="archives"></param>
        public void EnumerateDataArchives(IEnumerable<string> archives, string outDir, bool applyPatches = true, string mode = "blp", bool overwrite = true)
        {
            Log.WriteLine("Exporting Data Archive files");

            foreach (var archivename in archives)
            {
                if (archivename.ToLowerInvariant().Contains("data\\world"))
                {
                    Log.WriteLine("Skipping " + Path.GetFileName(archivename));
                    continue;
                }

                // check if mpq size > 0
                if (new FileInfo(archivename).Length == 0)
                {
                    Log.WriteLine("Skipping " + Path.GetFileName(archivename) + " (empty)");
                    continue;
                }

                using var mpq = new MpqArchive(archivename, FileAccess.Read);
                Log.WriteLine("   Exporting " + Path.GetFileName(mpq.FilePath));

                if (TryGetListFile(mpq, out var files))
                {
                    if (applyPatches)
                        mpq.AddPatchArchives(PatchArchives);

                    ExportFiles(mpq, files, outDir, mode, overwrite).Wait();
                    mpq.Dispose();
                }
                else if (TryReadAlpha(mpq, archivename))
                {
                    mpq.Dispose();
                }
                else
                {
                    Console.WriteLine("!!!!! - " + Path.GetFileName(archivename) + " HAS NO LISTFILE!");
                }
            }
        }

        /// <summary>
        /// Iterates all the patch archives, extracting and BLT encoding all new files
        /// NOTE: Looks like stormlib handles this automatically with AddPatchArchive(s)
        /// </summary>
        public void EnumeratePatchArchives()
        {
            throw new Exception("Don't use this");
            if (PatchArchives.Count == 0)
                return;

            Log.WriteLine("Exporting Patch Archive files");

            while (PatchArchives.Count > 0)
            {
                using var mpq = new MpqArchive(PatchArchives.Dequeue(), FileAccess.Read);
                Log.WriteLine("    Exporting " + Path.GetFileName(mpq.FilePath));

                if (!TryGetListFile(mpq, out var files))
                    throw new Exception(Path.GetFileName(mpq.FilePath) + " MISSING LISTFILE");

                mpq.AddPatchArchives(PatchArchives);
                ExportFiles(mpq, files, "", "blp", true).Wait();
                mpq.Dispose();
            }
        }

        /// <summary>
        /// Iterates all loose files within the data directory and BLT encodes them
        /// </summary>
        /// <param name="filenames"></param>
        public void EnumerateLooseDataFiles(IEnumerable<string> filenames)
        {
            if (!filenames.Any())
                return;

            Log.WriteLine("Exporting loose Data files");

            var block = new ActionBlock<string>(file =>
            {
                var filename = GetInternalPath(file);

                FileList.Add(filename);
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 150 });

            foreach (var f in filenames)
                block.Post(f);

            block.Complete();
            block.Completion.Wait();
        }

        #region Helpers

        /// <summary>
        /// Extracts a collection of files from an archive and BLTE encodes them
        /// </summary>
        /// <param name="mpq"></param>
        /// <param name="filenames"></param>
        /// <param name="maxDegreeOfParallelism"></param>
        /// <returns></returns>
        private async Task ExportFiles(MpqArchive mpq, IEnumerable<string> filenames, string outDir, string mode, bool overwrite, int maxDegreeOfParallelism = 150)
        {
            var exportedCount = 0;

            var block = new ActionBlock<string>(file =>
            {
                using var fs = mpq.OpenFile(file);

                if (fs == null)
                    return;

                // ignore PTCH files
                if (fs.Flags.HasFlag(MPQFileAttributes.PatchFile))
                    return;

                // patch has marked file for deletion so remove from filelist
                if (fs.Flags.HasFlag(MPQFileAttributes.DeleteMarker))
                {
                    FileList.Remove(file);
                    return;
                }

                if (fs.CanRead && fs.Length > 0)
                {
                    FileList.Add(file);
                }

                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);

                    File.WriteAllBytes(Path.Combine(outDir, file), ms.ToArray());

                    exportedCount++;
                }
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism });

            // This shouldn't be required I think but it is for 0.5.3 at least
            var filenamesEscaped = filenames.Select(f => f.Replace("/", "\\")).ToList();

            var filenamesFiltered = filenamesEscaped.Where(f => f.StartsWith("textures\\minimap", StringComparison.InvariantCultureIgnoreCase)).ToList();

            if (mode == "trs")
            {
                filenamesFiltered = filenamesFiltered.Where(f => f.EndsWith(".trs", StringComparison.InvariantCultureIgnoreCase)).ToList();
            }
            else if (mode == "blp")
            {
                filenamesFiltered = filenamesFiltered.Where(f => f.EndsWith(".blp", StringComparison.InvariantCultureIgnoreCase)).ToList();

                if (!overwrite)
                {
                    var existingTiles = Directory.GetFiles(Path.Combine(outDir, "textures"), "*.blp", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).ToList();
                    filenamesFiltered = filenamesFiltered.Where(f => !existingTiles.Contains(Path.GetFileName(f))).ToList();
                }
            }

            foreach (var file in filenamesFiltered)
            {
                if (!FileList.Contains(file))
                {
                    block.Post(file);
                }
            }

            block.Complete();
            await block.Completion;

            Log.WriteLine("    Exported " + exportedCount + " files");
        }

        /// <summary>
        /// Some alpha MPQs are hotswappable and only contain a single file and it's checksum
        /// </summary>
        /// <param name="mpq"></param>
        /// <param name="archivename"></param>
        private bool TryReadAlpha(MpqArchive mpq, string archivename)
        {
            // strip the local path and extension to get the filename
            var file = Path.ChangeExtension(GetInternalPath(archivename), null).WoWNormalise();

            if (FileList.Contains(file))
                return true;

            // add the filename as the listfile
            var internalname = Path.GetFileName(file);
            mpq.AddListFile(internalname);

            // read file if known
            if (mpq.HasFile(internalname))
            {
                using var fs = mpq.OpenFile(internalname);

                if (fs.CanRead && fs.Length > 0)
                {
                    FileList.Add(file);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Attempts to read the listfile if present
        /// </summary>
        /// <param name="mpq"></param>
        /// <param name="filteredlist"></param>
        /// <returns></returns>
        private bool TryGetListFile(MpqArchive mpq, out List<string> filteredlist)
        {
            filteredlist = new List<string>();

            if (mpq.HasFile(LISTFILE_NAME))
            {
                using (var file = mpq.OpenFile(LISTFILE_NAME))
                using (var sr = new StreamReader(file))
                {
                    if (!file.CanRead || file.Length <= 1)
                        return false;

                    while (!sr.EndOfStream)
                        filteredlist.Add(sr.ReadLine().WoWNormalise());
                }

                // remove the MPQ documentation files
                filteredlist.RemoveAll(RemoveUnwantedFiles);
                filteredlist.TrimExcess();

                return filteredlist.Count > 0;
            }

            return false;
        }

        private bool RemoveUnwantedFiles(string value)
        {
            value = value.ToLower();

            return value.StartsWith('(') ||
                value.StartsWith("component.") ||
                (value.EndsWith(".lst") && !value.StartsWith("triallists")) || // NOTE this includes wotlk alpha temp lists!
                HasDirectory(value) ||
                HasExtension(value);
        }

        private bool HasDirectory(string path)
        {
            return Options.ExcludedDirectories.Overlaps(path.Split(Path.DirectorySeparatorChar));
        }

        private bool HasExtension(string path)
        {
            return Options.ExcludedExtensions.Contains(Path.GetExtension(path) ?? "");
        }

        private string GetInternalPath(string value)
        {
            var index = value.IndexOf(DataDirectory, StringComparison.OrdinalIgnoreCase);
            index += DataDirectory.Length;
            return value[index..].WoWNormalise();
        }

        #endregion
    }
}
