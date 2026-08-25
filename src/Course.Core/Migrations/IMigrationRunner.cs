namespace Course.Core.Migrations;

public interface IMigrationRunner
{
    Task<MigrationApplyResult> ApplyAsync(string directoryPath, CancellationToken cancellationToken);
}

public sealed record MigrationApplyResult(IReadOnlyList<string> AppliedFiles, IReadOnlyList<string> SkippedFiles);

public sealed class MigrationConflictException : Exception
{
    public MigrationConflictException(string fileName)
        : base($"applied migration file changed: {fileName}")
    {
        FileName = fileName;
    }

    public string FileName { get; }
}
