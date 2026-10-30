using Microsoft.Data.Sqlite;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Le SQLite natif réellement chargé doit être postérieur au correctif de CVE-2025-6965
/// (GHSA-2m69-gcr7-jv3q, SQLite &lt; 3.50.2). Garde-fou contre un retour en arrière des
/// dépendances SQLitePCLRaw.
/// </summary>
public class SqliteVersionTests
{
    [Fact]
    public void Sqlite_charge_est_corrige_pour_CVE_2025_6965()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)cmd.ExecuteScalar()!);
        Assert.True(version >= new Version(3, 50, 2), $"SQLite {version} < 3.50.2");
    }
}
