using System.IO;

namespace SpotifyTaskbarWidget;

/// <summary>
/// ВРЕМЕННОЕ: переключатели для поиска утечки нативной памяти (31.10.2026).
/// Виджет набирает ~60 МБ в час, управляемая куча при этом 10 МБ, то есть
/// течёт что-то нативное. Внедрённые в процесс чужие библиотеки (RTSS,
/// Windhawk, Bonjour) проверены отсевом и невиновны, значит причина в нашем
/// коде - осталось понять, в какой его части.
///
/// Включается файлом рядом с exe, как и bridge-debug.txt. Проверка одна, при
/// старте; какие переключатели сработали, видно в errors.log.
///
/// Удалить вместе с местами применения, как только утечка найдена.
/// </summary>
internal static class TestSwitches
{
    private static bool Flag(string name) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, name));

    /// <summary>Не опрашивать панель задач через UI Automation.</summary>
    public static readonly bool NoUia = Flag("no-uia.txt");

    /// <summary>Не искать полноэкранное окно (перебор окон 5 раз в секунду).</summary>
    public static readonly bool NoFullscreen = Flag("no-fullscreen.txt");

    /// <summary>Не поднимать системную медиасессию.</summary>
    public static readonly bool NoSmtc = Flag("no-smtc.txt");

    /// <summary>Не поднимать мост к расширению браузера.</summary>
    public static readonly bool NoBridge = Flag("no-bridge.txt");

    public static void LogActive()
    {
        var on = new System.Collections.Generic.List<string>();
        if (NoUia) on.Add("no-uia");
        if (NoFullscreen) on.Add("no-fullscreen");
        if (NoSmtc) on.Add("no-smtc");
        if (NoBridge) on.Add("no-bridge");
        if (on.Count > 0)
            Diag.Log("TEST SWITCHES ACTIVE: " + string.Join(", ", on));
    }
}
