using Microsoft.Data.Sqlite;
using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Base de l'index : une réindexation (UPSERT) ne doit jamais effacer UseCount ni LastUsed.
/// </summary>
public class IndexDatabaseTests
{
    private static SqliteConnection OpenMemory()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var schema = conn.CreateCommand();
        schema.CommandText = IndexingService.SchemaSql;
        schema.ExecuteNonQuery();
        return conn;
    }

    private static void Upsert(SqliteConnection conn, string path, string name, string stamp)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexingService.UpsertItemSql;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@desc", path);
        cmd.Parameters.AddWithValue("@type", 2);
        cmd.Parameters.AddWithValue("@indexed", stamp);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Upsert_conserve_UseCount_et_LastUsed()
    {
        using var conn = OpenMemory();
        Upsert(conn, @"C:\Docs\a.txt", "a", "2026-01-01T00:00:00.0000000Z");

        using (var usage = conn.CreateCommand())
        {
            usage.CommandText = "UPDATE Items SET UseCount = 3, LastUsed = '2026-02-01T00:00:00.0000000Z' WHERE Path = @p";
            usage.Parameters.AddWithValue("@p", @"C:\Docs\a.txt");
            usage.ExecuteNonQuery();
        }

        // Réindexation : métadonnées rafraîchies, usage intact
        Upsert(conn, @"C:\Docs\a.txt", "a-renomme", "2026-03-01T00:00:00.0000000Z");

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT Name, UseCount, LastUsed, IndexedAt FROM Items WHERE Path = @p";
        read.Parameters.AddWithValue("@p", @"C:\Docs\a.txt");
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("a-renomme", reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
        Assert.Equal("2026-02-01T00:00:00.0000000Z", reader.GetString(2));
        Assert.Equal("2026-03-01T00:00:00.0000000Z", reader.GetString(3));
    }
}
