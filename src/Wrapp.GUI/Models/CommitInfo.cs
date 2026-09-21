using System;
using System.Collections.Generic;
using System.Linq;

namespace Wrapp.Models;

public class CommitInfo
{
    private const int MaxVisibleFiles = 8;

    public string Hash { get; init; } = "";
    public string FullHash { get; init; } = "";
    public string Author { get; init; } = "";
    public string RelativeTime { get; init; } = "";
    public string Message { get; init; } = "";

    // File change statistics
    public int FilesChanged { get; set; }
    public int Insertions { get; set; }
    public int Deletions { get; set; }

    // Per-file change list (populated by GetCommitLogAsync)
    public List<CommitFileChange> FileChanges { get; set; } = new();

    // Capped list for display (up to ~3 rows of tags)
    public IReadOnlyList<CommitFileChange> VisibleFileChanges =>
        FileChanges.Count <= MaxVisibleFiles ? FileChanges : FileChanges.Take(MaxVisibleFiles).ToList();

    public int OverflowCount => Math.Max(0, FileChanges.Count - MaxVisibleFiles);
    public bool HasMoreFileChanges => OverflowCount > 0;
    public string OverflowLabel => $"+ {OverflowCount} more";

    // Graph rendering flags
    public bool IsFirst { get; set; }
    public bool IsLast { get; set; }

    public string StatsDisplay => FilesChanged == 0 && Insertions == 0 && Deletions == 0
        ? string.Empty
        : $"{FilesChanged}f  +{Insertions}  -{Deletions}";
}
