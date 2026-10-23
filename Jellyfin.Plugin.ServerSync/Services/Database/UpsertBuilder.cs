#pragma warning disable CA2100 // Table and column names are internal constants; values are parameters.
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Builds the insert-or-update every sync table writes its rows with, from one list of columns and
/// how each is updated. Each table used to spell the statement out by hand, and the copies drifted:
/// one table let a refresh overwrite an Ignored row's retry count while the others kept it. The rules
/// that every table shares live in <see cref="SyncColumns"/> now, written once.
/// </summary>
public sealed class UpsertBuilder
{
    private readonly string _table;
    private readonly List<Column> _columns = new();

    /// <summary>Initializes a new instance of the <see cref="UpsertBuilder"/> class.</summary>
    /// <param name="table">The table, an internal constant.</param>
    public UpsertBuilder(string table)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);
        _table = table;
    }

    private enum Rule
    {
        // Part of the conflict target, so only ever inserted.
        Key,

        // Always takes the new value.
        Always,

        // Keeps the stored value on an Ignored row, so the user's choice and its reason survive scans.
        UnlessIgnored,

        // Keeps the stored value when the new one is null, as a synced hash is only known after an apply.
        UnlessNull,

        // Both of the above.
        UnlessIgnoredOrNull,

        // Takes the new value only when a condition holds.
        When
    }

    /// <summary>Gets the statement, for tests.</summary>
    public string Sql => BuildSql();

    /// <summary>Adds a column of the conflict target.</summary>
    /// <param name="column">The column.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public UpsertBuilder Key(string column, object? value) => Add(column, value, Rule.Key);

    /// <summary>Adds a column that always takes the new value.</summary>
    /// <param name="column">The column.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public UpsertBuilder Set(string column, object? value) => Add(column, value, Rule.Always);

    /// <summary>Adds a column that keeps its stored value on an Ignored row.</summary>
    /// <param name="column">The column.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public UpsertBuilder SetUnlessIgnored(string column, object? value) => Add(column, value, Rule.UnlessIgnored);

    /// <summary>Adds a column that keeps its stored value when the new one is null.</summary>
    /// <param name="column">The column.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public UpsertBuilder SetUnlessNull(string column, object? value) => Add(column, value, Rule.UnlessNull);

    /// <summary>Adds a column that keeps its stored value on an Ignored row or when the new one is null.</summary>
    /// <param name="column">The column.</param>
    /// <param name="value">Its value.</param>
    /// <returns>This builder.</returns>
    public UpsertBuilder SetUnlessIgnoredOrNull(string column, object? value) => Add(column, value, Rule.UnlessIgnoredOrNull);

    /// <summary>
    /// Adds a column that takes the new value only when a condition holds. The condition is SQL over
    /// the stored row's columns and the new values, which are named <c>@p_</c> and the column.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <param name="value">Its value.</param>
    /// <param name="condition">The condition, an internal constant.</param>
    /// <returns>This builder.</returns>
    public UpsertBuilder SetWhen(string column, object? value, string condition)
    {
        ArgumentException.ThrowIfNullOrEmpty(condition);
        _columns.Add(new Column(column, value, Rule.When, condition));
        return this;
    }

    /// <summary>
    /// Adds the columns every sync row carries: its status and the bookkeeping around it, which an
    /// Ignored row keeps, and the server it came from.
    /// </summary>
    /// <param name="record">The row.</param>
    /// <returns>This builder.</returns>
    public UpsertBuilder SyncColumns(SyncRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return SetUnlessIgnored("Status", (int)record.Status)
            .SetUnlessIgnored("StatusDate", Time(record.StatusDate))
            .SetUnlessIgnored("LastSyncTime", Time(record.LastSyncTime))
            .SetUnlessIgnored("Reason", record.Reason)
            .SetUnlessIgnored("RetryCount", record.RetryCount)
            .Set("ServerKey", record.ServerKey);
    }

    /// <summary>Formats a time the way every table stores it.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The stored text.</returns>
    public static string Time(DateTime value) => Utilities.UtcTime.Format(value);

    /// <summary>Formats a time the way every table stores it, or null.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The stored text, or null.</returns>
    public static string? Time(DateTime? value) => value.HasValue ? Utilities.UtcTime.Format(value.Value) : null;

    /// <summary>Stores a flag as SQLite stores booleans, or null.</summary>
    /// <param name="value">The flag.</param>
    /// <returns>1, 0, or null.</returns>
    public static int? Flag(bool? value) => value.HasValue ? (value.Value ? 1 : 0) : null;

    /// <summary>Runs the statement.</summary>
    /// <param name="connection">The connection, under the write lock.</param>
    public void Execute(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildSql();
        foreach (var column in _columns)
        {
            cmd.Parameters.AddWithValue(Param(column.Name), column.Value ?? DBNull.Value);
        }

        cmd.Parameters.AddWithValue("@ignoredStatus", (int)SyncStatus.Ignored);
        cmd.ExecuteNonQuery();
    }

    private static string Param(string column) => "@p_" + column;

    private UpsertBuilder Add(string column, object? value, Rule rule)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        _columns.Add(new Column(column, value, rule, null));
        return this;
    }

    private string BuildSql()
    {
        var keys = _columns.Where(c => c.Rule == Rule.Key).Select(c => c.Name).ToList();
        if (keys.Count == 0)
        {
            throw new InvalidOperationException($"An upsert into {_table} needs at least one key column");
        }

        var names = string.Join(", ", _columns.Select(c => c.Name));
        var values = string.Join(", ", _columns.Select(c => Param(c.Name)));
        var updates = string.Join(",\n    ", _columns.Where(c => c.Rule != Rule.Key).Select(UpdateOf));
        return $"INSERT INTO {_table} ({names})\nVALUES ({values})\nON CONFLICT({string.Join(", ", keys)}) DO UPDATE SET\n    {updates}";
    }

    private string UpdateOf(Column column)
    {
        var stored = $"{_table}.{column.Name}";
        var incoming = Param(column.Name);
        var ignored = $"{_table}.Status = @ignoredStatus";
        var expression = column.Rule switch
        {
            Rule.Always => incoming,
            Rule.UnlessIgnored => $"CASE WHEN {ignored} THEN {stored} ELSE {incoming} END",
            Rule.UnlessNull => $"CASE WHEN {incoming} IS NOT NULL THEN {incoming} ELSE {stored} END",
            Rule.UnlessIgnoredOrNull => $"CASE WHEN {ignored} THEN {stored} WHEN {incoming} IS NOT NULL THEN {incoming} ELSE {stored} END",
            Rule.When => $"CASE WHEN {column.Condition} THEN {incoming} ELSE {stored} END",
            _ => throw new InvalidOperationException($"No update for a key column {column.Name}")
        };
        return $"{column.Name} = {expression}";
    }

    private sealed record Column(string Name, object? Value, Rule Rule, string? Condition);
}
