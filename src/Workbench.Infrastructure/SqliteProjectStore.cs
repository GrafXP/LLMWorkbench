using Microsoft.Data.Sqlite;
using Workbench.Core;

namespace Workbench.Infrastructure;

public sealed class SqliteProjectStore : IProjectStore, IDisposable
{
    private readonly string _connectionString;
    private readonly FileStream _ownership;
    private readonly object _writeGate = new();
    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public SqliteProjectStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "project.sqlite");
        var existing = File.Exists(DatabasePath);
        _ownership = new FileStream(DatabasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
        try
        {
            using var db = Open();
            using (var version = db.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                var number = Convert.ToInt32(version.ExecuteScalar());
                if (existing && number != 1) throw new InvalidDataException($"This file is not a supported workbench project (schema {number}).");
            }
            Execute(db, null, "PRAGMA journal_mode=WAL;");
            Execute(db, null, """
                CREATE TABLE IF NOT EXISTS project (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS revisions (id TEXT PRIMARY KEY, json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS runs (id TEXT PRIMARY KEY, command_id TEXT NOT NULL UNIQUE, json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY, run_id TEXT NOT NULL REFERENCES runs(id), node_id TEXT NOT NULL, state TEXT NOT NULL, json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS jobs_run ON jobs(run_id);
                CREATE TABLE IF NOT EXISTS artifacts (id TEXT PRIMARY KEY, json TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
            RecoverInterrupted();
        }
        catch { _ownership.Dispose(); throw; }
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        Execute(db, null, "PRAGMA foreign_keys=ON;");
        return db;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] values)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        return command.ExecuteNonQuery();
    }
    private static T[] Read<T>(SqliteConnection db, string sql, params (string, object)[] values)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(DocumentJson.Read<T>(reader.GetString(0)));
        return [.. result];
    }
    public ProjectDocument? LoadProject() { using var db = Open(); return Read<ProjectDocument>(db, "SELECT json FROM project WHERE id=1").SingleOrDefault(); }
    public void SaveProject(ProjectDocument project)
    {
        GraphValidator.Validate(project.Graph);
        lock (_writeGate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            var existing = Read<ProjectDocument>(db, "SELECT json FROM project WHERE id=1").SingleOrDefault();
            if (existing is not null && existing.Id != project.Id) throw new InvalidOperationException("Save this project in its own directory.");
            Execute(db, tx, "INSERT INTO revisions(id,json) VALUES($id,$json) ON CONFLICT(id) DO NOTHING", ("$id", project.Graph.Id.ToString()), ("$json", DocumentJson.Write(project.Graph)));
            Execute(db, tx, "INSERT INTO project(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json", ("$json", DocumentJson.Write(project)));
            tx.Commit();
        }
    }
    public RunSnapshot Submit(ProjectDocument project, Guid commandId)
    {
        lock (_writeGate)
        {
            using var db = Open();
            var previous = Read<RunSnapshot>(db, "SELECT json FROM runs WHERE command_id=$command", ("$command", commandId.ToString())).SingleOrDefault();
            if (previous is not null) return previous;
            var sorted = GraphValidator.Validate(project.Graph, requireInputs: true);
            var run = new RunSnapshot(Guid.NewGuid(), commandId, project.Id, DocumentJson.Copy(project.Graph), DateTimeOffset.UtcNow);
            using var tx = db.BeginTransaction();
            Execute(db, tx, "INSERT INTO runs(id,command_id,json) VALUES($id,$command,$json)", ("$id", run.Id.ToString()), ("$command", commandId.ToString()), ("$json", DocumentJson.Write(run)));
            foreach (var node in sorted)
            {
                var job = new JobRecord(Guid.NewGuid(), run.Id, node.Id, JobState.Queued, new(), [], 0, "Queued");
                WriteJob(db, tx, job, insert: true);
            }
            tx.Commit();
            return run;
        }
    }
    public RunSnapshot[] LoadRuns() { using var db = Open(); return Read<RunSnapshot>(db, "SELECT json FROM runs ORDER BY rowid DESC"); }
    public JobRecord[] LoadJobs(Guid runId) { using var db = Open(); return Read<JobRecord>(db, "SELECT json FROM jobs WHERE run_id=$run ORDER BY rowid", ("$run", runId.ToString())); }
    public Artifact[] LoadArtifacts() { using var db = Open(); return Read<Artifact>(db, "SELECT json FROM artifacts ORDER BY rowid"); }
    public void SaveJob(JobRecord job)
    {
        lock (_writeGate) { using var db = Open(); WriteJob(db, null, job); }
    }
    private static void WriteJob(SqliteConnection db, SqliteTransaction? tx, JobRecord job, bool insert = false)
    {
        var changed = Execute(db, tx,
        insert ? "INSERT INTO jobs(id,run_id,node_id,state,json) VALUES($id,$run,$node,$state,$json)" : "UPDATE jobs SET state=$state,json=$json WHERE id=$id AND run_id=$run AND node_id=$node",
        ("$id", job.Id.ToString()), ("$run", job.RunId.ToString()), ("$node", job.NodeId.ToString()), ("$state", job.State.ToString()), ("$json", DocumentJson.Write(job)));
        if (changed != 1) throw new InvalidOperationException("The saved job no longer exists.");
    }
    public void Complete(JobRecord job, Artifact[] artifacts)
    {
        lock (_writeGate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            foreach (var artifact in artifacts)
            {
                var stored = artifact;
                if (artifact.Kind == ArtifactKind.Text)
                {
                    var relative = Path.Combine("artifacts", artifact.Id + ".txt");
                    var path = Path.Combine(DirectoryPath, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".tmp";
                    File.WriteAllText(temporary, artifact.Content);
                    File.Move(temporary, path, overwrite: false);
                    stored = artifact with { RelativePath = relative };
                }
                Execute(db, tx, "INSERT INTO artifacts(id,json) VALUES($id,$json)", ("$id", stored.Id.ToString()), ("$json", DocumentJson.Write(stored)));
            }
            WriteJob(db, tx, job);
            tx.Commit();
        }
    }
    public void RecoverInterrupted()
    {
        lock (_writeGate)
        {
            using var db = Open();
            var active = Read<JobRecord>(db, "SELECT json FROM jobs WHERE state='Running'");
            using var tx = db.BeginTransaction();
            foreach (var job in active) WriteJob(db, tx, job with { State = JobState.Interrupted, Message = "Interrupted during the previous session. Retry to continue.", FinishedUtc = DateTimeOffset.UtcNow });
            tx.Commit();
        }
    }
    public void Dispose() => _ownership.Dispose();
}
