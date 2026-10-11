using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using XtreamPlayer.Application;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Server;

public sealed class ServerStore
{
    private readonly string _connection;
    private readonly IDataProtector _protector;
    public ServerStore(ServerOptions options, IDataProtectionProvider protection)
    {
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.MediaDirectory);
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.DataDirectory, "server.sqlite"), DefaultTimeout = 30 }.ToString();
        _protector = protection.CreateProtector("XtreamPlayer.Server.Store.v1");
        using var db = Open();
        db.Execute("PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS entries (kind TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY(kind,id));");
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public List<T> List<T>(string kind)
    {
        using var db = Open();
        return db.Query<string>("SELECT body FROM entries WHERE kind=@kind", new { kind })
            .Select(s => JsonSerializer.Deserialize<T>(_protector.Unprotect(s))!).ToList();
    }
    public T? Get<T>(string kind, string id)
    {
        using var db = Open();
        var body = db.QuerySingleOrDefault<string>("SELECT body FROM entries WHERE kind=@kind AND id=@id", new { kind, id });
        return body is null ? default : JsonSerializer.Deserialize<T>(_protector.Unprotect(body));
    }
    public void Put<T>(string kind, string id, T value)
    {
        var body = _protector.Protect(JsonSerializer.Serialize(value));
        using var db = Open();
        db.Execute("INSERT INTO entries VALUES (@kind,@id,@body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body", new { kind, id, body });
    }
    public void PutMany(string kind, IReadOnlyDictionary<string, string> values)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        foreach (var (id, value) in values)
        {
            var body = _protector.Protect(JsonSerializer.Serialize(value));
            db.Execute("INSERT INTO entries VALUES (@kind,@id,@body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body",
                new { kind, id, body }, transaction);
        }
        transaction.Commit();
    }
    public void Delete(string kind, string id)
    {
        using var db = Open(); db.Execute("DELETE FROM entries WHERE kind=@kind AND id=@id", new { kind, id });
    }
    public void DeletePrefix(string kind, string prefix)
    {
        using var db = Open();
        db.Execute("DELETE FROM entries WHERE kind=@kind AND substr(id,1,length(@prefix))=@prefix", new { kind, prefix });
    }
    public Profile Profile(string id) => Get<Profile>("profile", id) ?? throw new KeyNotFoundException();
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public string? Authenticate(string token) => Get<DeviceRecord>("device", Hash(token))?.Id;
    public sealed record DeviceRecord(string Id, string Name, DateTimeOffset CreatedAt);
}
