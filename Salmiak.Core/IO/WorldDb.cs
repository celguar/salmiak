using System;
using System.Collections.Generic;
using MySqlConnector;

namespace Salmiak.Core.IO;

public sealed class DbSettings
{
    public string Host = "127.0.0.1";
    public int Port = 3306;
    public string User = "mangos";
    public string Password = "mangos";
    public string WorldDb = "classicmangos";
    public string CharactersDb = "classiccharacters";
}

public sealed class WorldDb
{
    public DbSettings Settings { get; }

    public WorldDb(DbSettings settings) { Settings = settings; }

    private string ConnString(string? database) =>
        $"Server={Settings.Host};Port={Settings.Port};User ID={Settings.User};Password={Settings.Password};" +
        (database != null ? $"Database={database};" : "") +
        "Connection Timeout=5;AllowUserVariables=true";

    public string? TestConnection(out string serverInfo)
    {
        serverInfo = "";
        try
        {
            using var c = new MySqlConnection(ConnString(null));
            c.Open();
            serverInfo = $"MySQL {c.ServerVersion}";
            foreach (var db in new[] { Settings.WorldDb, Settings.CharactersDb })
            {
                using var cmd = new MySqlCommand("SHOW DATABASES LIKE @db", c);
                cmd.Parameters.AddWithValue("@db", db);
                if (cmd.ExecuteScalar() == null) return $"database '{db}' not found";
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public List<Dictionary<string, object?>> Query(string sql, bool characters = false,
                                                   params (string Name, object? Value)[] args)
    {
        using var c = new MySqlConnection(ConnString(characters ? Settings.CharactersDb : Settings.WorldDb));
        c.Open();
        using var cmd = new MySqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>(r.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public int Execute(string sql, bool characters = false, params (string Name, object? Value)[] args)
    {
        using var c = new MySqlConnection(ConnString(characters ? Settings.CharactersDb : Settings.WorldDb));
        c.Open();
        using var cmd = new MySqlCommand(sql, c);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }
}
