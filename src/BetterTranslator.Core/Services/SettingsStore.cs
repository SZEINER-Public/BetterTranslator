using System.Globalization;
using BetterTranslator.Core.Models;
using Microsoft.Data.Sqlite;

namespace BetterTranslator.Core.Services;

/// <summary>
/// Settings live in the same local store as everything else, one row per key,
/// so adding a setting needs no migration.
/// </summary>
public sealed class SettingsStore(Database database)
{
    private const string LearnFromMyEdits = "learn_from_my_edits";
    private const string UnderlineMemoryWords = "underline_memory_words";
    private const string ReindexOnChange = "reindex_files_when_they_change";
    private const string DefaultScope = "default_scope_for_new_chats";
    private const string UnsureThreshold = "unsure_threshold_percent";
    private const string TargetLanguage = "target_language";
    private const string RuntimeBackendKey = "runtime_backend";
    private const string SelectedModelPathKey = "selected_model_path";
    private const string SelectedModelFileKey = "selected_model_file";
    private const string ModelChosenExplicitlyKey = "selected_model_explicit";
    private const string EffortKey = "translation_effort";
    private const string DomainVocabularyKey = "use_domain_vocabulary";
    private const string TemperatureKey = "translation_temperature";
    private const string InstructionKey = "translation_instruction";
    private const string VerifyEnabled = "verification_enabled";
    private const string VerifyDic = "verification_hunspell_dic_path";
    private const string VerifyAff = "verification_hunspell_aff_path";
    private const string VerifyMajkaExe = "verification_majka_exe_path";
    private const string VerifyMajkaDict = "verification_majka_dict_path";
    private const string VerifyFrequency = "verification_frequency_list_path";
    private const string VerifyWarning = "verification_warning_threshold";
    private const string VerifyError = "verification_error_threshold";
    private const string VerifyNgramFloor = "verification_ngram_logprob_floor";
    private const string VerifyMinFrequency = "verification_min_frequency";
    private const string VerifyChunkRun = "verification_untranslated_chunk_min_run";
    private const string GateEnabled = "verification_gate_enabled";
    private const string GateRepairCap = "verification_gate_repair_candidate_cap";
    private const string GateUnitCap = "verification_gate_findings_per_unit_cap";
    private const string GateRouting = "verification_gate_routing";
    private const string GateStages = "verification_gate_stages";
    private const string GateDisabled = "verification_gate_disabled_categories";
    private const string RestartAfterInstallKey = "restart_after_install";
    private const string McpEnabledKey = "mcp_enabled";
    private const string McpHostKey = "mcp_host";
    private const string McpPortKey = "mcp_port";
    private const string McpTokenKey = "mcp_token";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var settings = new AppSettings();

        return new AppSettings
        {
            LearnFromMyEdits = Bool(stored, LearnFromMyEdits, settings.LearnFromMyEdits),
            UseDomainVocabulary = Bool(stored, DomainVocabularyKey, settings.UseDomainVocabulary),
            UnderlineMemoryWords = Bool(stored, UnderlineMemoryWords, settings.UnderlineMemoryWords),
            ReindexFilesWhenTheyChange = Bool(stored, ReindexOnChange, settings.ReindexFilesWhenTheyChange),
            DefaultScopeForNewChats = stored.TryGetValue(DefaultScope, out var scope)
                && Enum.TryParse<ChatScope>(scope, out var parsed)
                    ? parsed
                    : settings.DefaultScopeForNewChats,
            UnsureThresholdPercent = Int(stored, UnsureThreshold, settings.UnsureThresholdPercent),
            TargetLanguage = stored.GetValueOrDefault(TargetLanguage, settings.TargetLanguage),
            RuntimeBackend = stored.TryGetValue(RuntimeBackendKey, out var backend)
                && Enum.TryParse<RuntimeBackend>(backend, out var backendParsed)
                    ? backendParsed
                    : settings.RuntimeBackend,
            SelectedModelPath = stored.GetValueOrDefault(SelectedModelPathKey, settings.SelectedModelPath),
            SelectedModelFile = ChosenFile(stored, settings.SelectedModelFile),
            ModelChosenExplicitly = Bool(
                stored,
                ModelChosenExplicitlyKey,
                stored.GetValueOrDefault(SelectedModelPathKey, string.Empty).Length > 0),
            Effort = EffortTiers.Parse(stored.GetValueOrDefault(EffortKey)) ?? settings.Effort,
            Temperature = Double(stored, TemperatureKey, settings.Temperature),
            Instruction = stored.GetValueOrDefault(InstructionKey, settings.Instruction),
            RestartAfterInstall = Bool(stored, RestartAfterInstallKey, settings.RestartAfterInstall),
            McpEnabled = Bool(stored, McpEnabledKey, settings.McpEnabled),
            McpHost = stored.GetValueOrDefault(McpHostKey, settings.McpHost),
            McpPort = Int(stored, McpPortKey, settings.McpPort),
            McpToken = stored.GetValueOrDefault(McpTokenKey, settings.McpToken),
            Verification = new Verification.VerificationSettings
            {
                Enabled = Bool(stored, VerifyEnabled, settings.Verification.Enabled),
                HunspellDicPath = Path(stored, VerifyDic),
                HunspellAffPath = Path(stored, VerifyAff),
                MajkaExePath = Path(stored, VerifyMajkaExe),
                MajkaDictPath = Path(stored, VerifyMajkaDict),
                FrequencyListPath = Path(stored, VerifyFrequency),
                WarningThreshold = Int(stored, VerifyWarning, settings.Verification.WarningThreshold),
                ErrorThreshold = Int(stored, VerifyError, settings.Verification.ErrorThreshold),
                NgramLogProbFloor = Double(stored, VerifyNgramFloor, settings.Verification.NgramLogProbFloor),
                MinFrequency = Int(stored, VerifyMinFrequency, (int)settings.Verification.MinFrequency),
                UntranslatedChunkMinRun = Int(stored, VerifyChunkRun, settings.Verification.UntranslatedChunkMinRun),
                Gate = new Verification.Gate.GateSettings
                {
                    Enabled = Bool(stored, GateEnabled, settings.Verification.Gate.Enabled),
                    RepairCandidateCap = Int(stored, GateRepairCap, settings.Verification.Gate.RepairCandidateCap),
                    FindingsPerUnitCap = Int(stored, GateUnitCap, settings.Verification.Gate.FindingsPerUnitCap),
                    RoutingText = stored.GetValueOrDefault(GateRouting, settings.Verification.Gate.RoutingText),
                    StageText = stored.GetValueOrDefault(GateStages, settings.Verification.Gate.StageText),
                    DisabledCategoriesText = stored.GetValueOrDefault(GateDisabled, settings.Verification.Gate.DisabledCategoriesText),
                },
            },
        };
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            [LearnFromMyEdits] = settings.LearnFromMyEdits ? "1" : "0",
            [DomainVocabularyKey] = settings.UseDomainVocabulary ? "1" : "0",
            [UnderlineMemoryWords] = settings.UnderlineMemoryWords ? "1" : "0",
            [ReindexOnChange] = settings.ReindexFilesWhenTheyChange ? "1" : "0",
            [DefaultScope] = settings.DefaultScopeForNewChats.ToString(),
            [UnsureThreshold] = settings.UnsureThresholdPercent.ToString(CultureInfo.InvariantCulture),
            [TargetLanguage] = settings.TargetLanguage,
            [RuntimeBackendKey] = settings.RuntimeBackend.ToString(),
            [SelectedModelPathKey] = settings.SelectedModelPath,
            [SelectedModelFileKey] = settings.SelectedModelFile,
            [ModelChosenExplicitlyKey] = settings.ModelChosenExplicitly ? "1" : "0",
            [EffortKey] = settings.Effort.ToString(),
            [TemperatureKey] = settings.Temperature.ToString(CultureInfo.InvariantCulture),
            [InstructionKey] = settings.Instruction,
            [VerifyEnabled] = settings.Verification.Enabled ? "1" : "0",
            [VerifyDic] = settings.Verification.HunspellDicPath ?? string.Empty,
            [VerifyAff] = settings.Verification.HunspellAffPath ?? string.Empty,
            [VerifyMajkaExe] = settings.Verification.MajkaExePath ?? string.Empty,
            [VerifyMajkaDict] = settings.Verification.MajkaDictPath ?? string.Empty,
            [VerifyFrequency] = settings.Verification.FrequencyListPath ?? string.Empty,
            [VerifyWarning] = settings.Verification.WarningThreshold.ToString(CultureInfo.InvariantCulture),
            [VerifyError] = settings.Verification.ErrorThreshold.ToString(CultureInfo.InvariantCulture),
            [VerifyNgramFloor] = settings.Verification.NgramLogProbFloor.ToString(CultureInfo.InvariantCulture),
            [VerifyMinFrequency] = settings.Verification.MinFrequency.ToString(CultureInfo.InvariantCulture),
            [VerifyChunkRun] = settings.Verification.UntranslatedChunkMinRun.ToString(CultureInfo.InvariantCulture),
            [GateEnabled] = settings.Verification.Gate.Enabled ? "1" : "0",
            [GateRepairCap] = settings.Verification.Gate.RepairCandidateCap.ToString(CultureInfo.InvariantCulture),
            [GateUnitCap] = settings.Verification.Gate.FindingsPerUnitCap.ToString(CultureInfo.InvariantCulture),
            [GateRouting] = settings.Verification.Gate.RoutingText,
            [GateStages] = settings.Verification.Gate.StageText,
            [GateDisabled] = settings.Verification.Gate.DisabledCategoriesText,
            [RestartAfterInstallKey] = settings.RestartAfterInstall ? "1" : "0",
            [McpEnabledKey] = settings.McpEnabled ? "1" : "0",
            [McpHostKey] = settings.McpHost,
            [McpPortKey] = settings.McpPort.ToString(CultureInfo.InvariantCulture),
            [McpTokenKey] = settings.McpToken,
        };

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (key, value) in values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO settings (key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = $v;
                """;
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static string ChosenFile(Dictionary<string, string> values, string fallback)
    {
        if (values.TryGetValue(SelectedModelFileKey, out var stored) && stored.Length > 0)
        {
            return stored;
        }

        var path = values.GetValueOrDefault(SelectedModelPathKey, string.Empty);

        return path.Length > 0 ? System.IO.Path.GetFileName(path) : fallback;
    }

    private static bool Bool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw) ? raw == "1" : fallback;

    private static string? Path(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) && raw.Length > 0 ? raw : null;

    private static double Double(Dictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out var raw) && double.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static int Int(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
