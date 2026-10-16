using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakroWerkstatt;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void WriteFixture(MacroStorage storage, MacroDocument document)
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    var path = Path.Combine(storage.MacroFolder, $"{document.Id:N}.json");
    File.WriteAllText(path, JsonSerializer.Serialize(document, options));
}

var storageRoot = Path.Combine(Path.GetTempPath(), $"MakroWerkstatt-SmokeTests-{Guid.NewGuid():N}");
var storage = new MacroStorage(storageRoot);
var id = Guid.NewGuid();
var legacyId = Guid.NewGuid();
var fixtureIds = new List<Guid> { legacyId };
var macro = new MacroDocument
{
    Id = id,
    Name = "Lokaler Smoke-Test",
    Actions =
    [
        new MacroAction { AtMs = 0, Type = MacroActionType.KeyDown, KeyCode = 65, ScanCode = 30 },
        new MacroAction { AtMs = 20, Type = MacroActionType.KeyUp, KeyCode = 65, ScanCode = 30, RawFlags = 128 }
    ]
};

try
{
    WriteFixture(storage, new MacroDocument
    {
        Id = legacyId,
        FormatVersion = 1,
        Name = "Version-1-Smoke-Test",
        Actions =
        [
            new MacroAction { AtMs = 0, Type = MacroActionType.MouseButtonDown, Button = MacroMouseButtons.Left },
            new MacroAction { AtMs = 5, Type = MacroActionType.MouseButtonUp, Button = MacroMouseButtons.X2 }
        ]
    });
    var legacyMacro = storage.LoadById(legacyId);
    Require(legacyMacro.FormatVersion == 1 && legacyMacro.Actions[0].Button == MacroMouseButtons.Left
        && legacyMacro.Actions[1].Button == MacroMouseButtons.X2,
        "Eine Makrodatei im Format Version 1 mit gültigen Maustasten wurde nicht unverändert geladen.");

    foreach (var button in MacroMouseButtons.AllowedValues)
    {
        var buttonId = Guid.NewGuid();
        fixtureIds.Add(buttonId);
        WriteFixture(storage, new MacroDocument
        {
            Id = buttonId,
            Name = "Gültige Maustaste",
            Actions = [new MacroAction { AtMs = 0, Type = MacroActionType.MouseButtonDown, Button = button }]
        });
        Require(storage.LoadById(buttonId).Actions[0].Button == button,
            $"Eine Makrodatei mit einer erlaubten Maustaste wurde nicht geladen.");
    }

    var invalidButtonId = Guid.NewGuid();
    fixtureIds.Add(invalidButtonId);
    const string invalidButton = "secret-in";
    WriteFixture(storage, new MacroDocument
    {
        Id = invalidButtonId,
        Name = "Ungültige Maustaste",
        Actions = [new MacroAction { AtMs = 0, Type = MacroActionType.MouseButtonDown, Button = invalidButton }]
    });
    try
    {
        storage.LoadById(invalidButtonId);
        throw new InvalidOperationException("Eine unbekannte Maustaste wurde beim Laden nicht abgelehnt.");
    }
    catch (InvalidDataException ex)
    {
        Require(ex.Message.Contains("Aktion 1", StringComparison.Ordinal)
            && ex.Message.Contains("unbekannte Maustaste", StringComparison.Ordinal)
            && !ex.Message.Contains(invalidButton, StringComparison.Ordinal),
            "Die Validierung nennt die unbekannte Maustaste nicht sicher und mit Aktionsindex.");
    }

    var nonMouseId = Guid.NewGuid();
    fixtureIds.Add(nonMouseId);
    WriteFixture(storage, new MacroDocument
    {
        Id = nonMouseId,
        Name = "Nicht-Mausaktion",
        Actions = [new MacroAction { AtMs = 0, Type = MacroActionType.KeyDown, Button = invalidButton }]
    });
    Require(storage.LoadById(nonMouseId).Actions.Count == 1,
        "Der Maustastenwert einer Nicht-Mausaktion wurde fälschlich validiert.");

    storage.Save(macro);
    var reopened = storage.LoadById(id);
    Require(reopened.FormatVersion == MacroStorage.CurrentFormatVersion,
        "Das gespeicherte Makro verwendet nicht die aktuelle Formatversion.");
    Require(reopened.Actions.Count == 2, "Die Aktionsanzahl stimmt nach dem Öffnen nicht.");
    Require(reopened.Actions[1].AtMs == 20 && reopened.Actions[1].Type == MacroActionType.KeyUp,
        "Reihenfolge, Zeitstempel oder Aktionstyp sind nach dem Öffnen falsch.");

    var plan = PlaybackPlan.Create(reopened, stretchEnabled: true, targetDurationMs: 200,
        randomizerEnabled: true, randomMinMs: 1, randomMaxMs: 5);
    Require(Math.Abs(plan.Scale - 10) < 0.001, "Der Streckfaktor ist nicht korrekt.");
    Require(Math.Abs(plan.EstimatedMinimumMs - 201) < 0.001 && Math.Abs(plan.EstimatedMaximumMs - 205) < 0.001,
        "Die Schätzung berücksichtigt die Randomizer-Spanne nicht korrekt.");

    var executed = new List<MacroAction>();
    var chosenDelays = new List<int>();
    var stopwatch = Stopwatch.StartNew();
    await MacroPlayback.RunAsync(plan, executed.Add, CancellationToken.None, chosenDelays.Add);
    stopwatch.Stop();
    Require(executed.Count == 2 && executed[0].KeyCode == 65 && executed[1].Type == MacroActionType.KeyUp,
        "Die Wiedergabe hat die Aktionen nicht einmalig und in Reihenfolge ausgeführt.");
    Require(chosenDelays.Count == 1 && chosenDelays[0] is >= 1 and <= 5,
        "Die zufällige Zusatzverzögerung lag außerhalb ihrer Spanne.");
    Require(stopwatch.ElapsedMilliseconds >= 190 && stopwatch.ElapsedMilliseconds < 2000,
        "Die gestreckte Testsequenz lief deutlich außerhalb ihrer Schätzung.");

    var untouchedPlan = PlaybackPlan.Create(reopened, stretchEnabled: false, targetDurationMs: 999,
        randomizerEnabled: false, randomMinMs: 1, randomMaxMs: 1);
    Require(Math.Abs(untouchedPlan.Scale - 1) < 0.001 && untouchedPlan.BaseDurationMs == 20,
        "Das Originaltempo wurde verändert.");

    var sameTimeMacro = new MacroDocument
    {
        Actions =
        [
            new MacroAction { AtMs = 0, Type = MacroActionType.MouseMove },
            new MacroAction { AtMs = 0, Type = MacroActionType.MouseWheel, WheelDelta = 120 }
        ]
    };
    var sameTimePlan = PlaybackPlan.Create(sameTimeMacro, stretchEnabled: true, targetDurationMs: 500,
        randomizerEnabled: false, randomMinMs: 1, randomMaxMs: 1);
    Require(sameTimePlan.EvenlyDistribute && sameTimePlan.DueTimeMs(1, sameTimeMacro.Actions[1]) == 500,
        "Gleichzeitige Aktionen ohne messbaren Abstand werden nicht über die Zielzeit verteilt.");

    var singleActionPlan = PlaybackPlan.Create(new MacroDocument
    {
        Actions = [new MacroAction { AtMs = 0, Type = MacroActionType.MouseMove }]
    }, stretchEnabled: true, targetDurationMs: 500, randomizerEnabled: false, randomMinMs: 1, randomMaxMs: 1);
    Require(singleActionPlan.EstimatedAverageMs == 0,
        "Eine einzelne sofortige Aktion darf nicht als volle Streckdauer geschätzt werden.");

    var viewModel = new MainViewModel(storage, new TestDialogService()) { InputHookAvailable = true };
    viewModel.StartRecordingCommand.Execute(null);
    Require(viewModel.RecordingState == RecordingState.Recording,
        "Der Aufnahmezustand wechselt beim Start nicht auf Aufnahme.");
    viewModel.ToggleRecordingPauseCommand.Execute(null);
    Require(viewModel.RecordingState == RecordingState.Paused,
        "Der Aufnahmezustand wechselt beim Pausieren nicht auf Pausiert.");
    viewModel.ToggleRecordingPauseCommand.Execute(null);
    Require(viewModel.RecordingState == RecordingState.Recording,
        "Der Aufnahmezustand wechselt beim Fortsetzen nicht zurück auf Aufnahme.");
    viewModel.StopRecordingCommand.Execute(null);
    Require(viewModel.RecordingState == RecordingState.Idle,
        "Der Aufnahmezustand wechselt beim Stoppen nicht auf Bereit.");
    viewModel.StartRecordingCommand.Execute(null);
    viewModel.DiscardRecordingCommand.Execute(null);
    Require(viewModel.RecordingState == RecordingState.Idle && viewModel.Actions.Count == 0,
        "Verwerfen stoppt die Aufnahme nicht oder lässt Aktionen zurück.");

    var logDirectory = Path.Combine(Path.GetTempPath(), $"MakroWerkstatt-logger-{Guid.NewGuid():N}");
    Directory.CreateDirectory(logDirectory);
    Exception simulatedCallbackError;
    try
    {
        throw new InvalidOperationException("private callback payload");
    }
    catch (Exception ex)
    {
        simulatedCallbackError = ex;
    }
    FileLogger.Initialize(logDirectory);
    FileLogger.Error("GlobalInputHook.KeyboardHookCallback", "KeyboardHook callback failed", simulatedCallbackError, 123);
    FileLogger.Shutdown();
    var logFile = Directory.GetFiles(logDirectory, "makrowerkstatt-*.log").Single();
    var logContents = File.ReadAllText(logFile);
    Require(logContents.Contains("GlobalInputHook.KeyboardHookCallback", StringComparison.Ordinal)
        && logContents.Contains("ExceptionType=InvalidOperationException", StringComparison.Ordinal)
        && logContents.Contains("Win32Error=123", StringComparison.Ordinal)
        && logContents.Contains("StackTrace:", StringComparison.Ordinal)
        && !logContents.Contains("private callback payload", StringComparison.Ordinal),
        "Der Logger schreibt technische Fehlerinformationen, lässt aber Exception-Inhalte weg.");
    if (Directory.Exists(logDirectory))
    {
        foreach (var path in Directory.GetFiles(logDirectory)) File.Delete(path);
        Directory.Delete(logDirectory);
    }

    var blockedLogDirectory = Path.Combine(Path.GetTempPath(), $"MakroWerkstatt-logger-{Guid.NewGuid():N}.file");
    File.WriteAllText(blockedLogDirectory, "file blocks directory creation");
    try
    {
        FileLogger.Initialize(blockedLogDirectory);
        FileLogger.Error("SmokeTests.FileLogger", "Simulated callback failure", new InvalidOperationException("test"));
        FileLogger.Shutdown();
    }
    catch
    {
        throw new InvalidOperationException("Der Logger darf bei einem nicht beschreibbaren Pfad keine Datei-Exception weitergeben.");
    }
    finally
    {
        if (File.Exists(blockedLogDirectory)) File.Delete(blockedLogDirectory);
    }

    Console.WriteLine("OK: JSON-Versionen, Maustastenvalidierung, Wiedergabeplanung, Aufnahmezustände und fehlertoleranter Logger.");
}

finally
{
    FileLogger.Shutdown();
    storage.Delete(id);
    foreach (var fixtureId in fixtureIds) storage.Delete(fixtureId);
    if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
}

sealed class TestDialogService : IDialogService
{
    public bool ConfirmRecordingConsent() => true;
    public bool ConfirmDiscardChanges() => true;
    public bool ConfirmDeleteMacro(string name) => true;
    public ClosePromptChoice ConfirmCloseWithUnsavedChanges() => ClosePromptChoice.Discard;
    public void Show(string message, string title, DialogKind kind) { }
}
