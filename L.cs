using System.Globalization;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Interface strings. The language comes from the user setting; on
/// "automatic" it follows Windows and falls back to English for everything else.
/// Argument order in <see cref="Pick"/>: en, pt, ru, uk.
/// The ru/uk translations are machine-made.
/// </summary>
internal static class L
{
    /// <summary>Supported codes; "" = automatic (follows Windows).</summary>
    public static readonly string[] Codes = { "en", "pt", "ru", "uk" };

    /// <summary>Language in use: the user's choice or, on automatic, the Windows
    /// one. Read on every access - switching language has to show up without
    /// restarting the app.</summary>
    public static string Current
    {
        get
        {
            string chosen = WidgetSettings.Shared.Language;
            if (!string.IsNullOrWhiteSpace(chosen))
                return chosen.ToLowerInvariant();
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        }
    }

    private static string Pick(string en, string pt, string ru, string uk) => Current switch
    {
        "pt" => pt,
        "ru" => ru,
        "uk" => uk,
        _ => en,
    };

    public const string AppTitle = "Taskbar Widget for Spotify";

    // Language
    public static string LanguageMenu => Pick("Language", "Idioma", "Язык", "Мова");
    public static string LanguageAuto => Pick(
        "Automatic (Windows)", "Automático (Windows)",
        "Автоматически (Windows)", "Автоматично (Windows)");

    // Menu
    public static string MoveWidget => Pick(
        "Move widget", "Mover widget", "Переместить виджет", "Перемістити віджет");
    public static string MoveWidgetTip => Pick(
        "Drag the widget wherever you want; untick to lock it in place",
        "Arrasta o widget para onde quiseres; desmarca para bloquear nessa posição",
        "Перетащите виджет куда хотите; снимите галочку, чтобы зафиксировать",
        "Перетягніть віджет куди завгодно; зніміть позначку, щоб зафіксувати");
    public static string ResetAutoPos => Pick(
        "Reset to automatic position", "Repor posição automática",
        "Сбросить на автоматическое положение", "Скинути на автоматичне положення");
    public static string MonitorMenu => Pick("Monitor", "Monitor", "Монитор", "Монітор");
    public static string MonitorPrimary => Pick("Primary", "Principal", "Основной", "Основний");
    public static string MonitorN(int n) => Pick(
        $"Monitor {n}", $"Monitor {n}", $"Монитор {n}", $"Монітор {n}");
    public static string MonitorHint => Pick(
        "There's only a taskbar on one screen. To move monitors, enable\n\"Show my taskbar on all displays\"\nin Settings → Personalization → Taskbar",
        "Só há barra de tarefas num ecrã. Para mudar de monitor, ativa\n\"Mostrar a minha barra de tarefas em todos os ecrãs\"\nem Definições → Personalização → Barra de tarefas",
        "Панель задач есть только на одном экране. Чтобы выбрать монитор, включите\n«Показывать панель задач на всех дисплеях»\nв Параметры → Персонализация → Панель задач",
        "Панель завдань є лише на одному екрані. Щоб вибрати монітор, увімкніть\n«Показувати панель завдань на всіх дисплеях»\nу Параметри → Персоналізація → Панель завдань");
    public static string SizeMenu => Pick("Size", "Tamanho", "Размер", "Розмір");
    public static string OpacityMenu => Pick("Brightness", "Brilho", "Яркость", "Яскравість");
    public static string SizeSmall => Pick("Small", "Pequeno", "Маленький", "Маленький");
    public static string SizeNormal => Pick("Normal", "Normal", "Обычный", "Звичайний");
    public static string SizeLarge => Pick("Large", "Grande", "Большой", "Великий");
    public static string FontMenu => Pick("Font", "Tipo de letra", "Шрифт", "Шрифт");
    public static string FontSystem => Pick(
        "System default", "Predefinido do sistema",
        "Системный по умолчанию", "Системний за умовчанням");
    public static string FontCustom => Pick(
        "Custom font...", "Outra fonte...", "Другой шрифт...", "Інший шрифт...");
    public static string FontCustomCurrent(string name) => Pick(
        $"Custom font: {name}...", $"Outra fonte: {name}...",
        $"Другой шрифт: {name}...", $"Інший шрифт: {name}...");
    public static string FontCustomTitle => Pick(
        "Choose a font", "Escolher tipo de letra", "Выбор шрифта", "Вибір шрифту");
    public static string FontCustomLabel => Pick(
        "Exact font name (e.g. JetBrains Mono):",
        "Nome exato da fonte (ex.: JetBrains Mono):",
        "Точное название шрифта (например, JetBrains Mono):",
        "Точна назва шрифту (наприклад, JetBrains Mono):");
    public static string FontCustomClear => Pick(
        "Leave empty to go back to the system font.",
        "Deixa vazio para voltar ao tipo de letra do sistema.",
        "Оставьте пустым, чтобы вернуть системный шрифт.",
        "Залиште порожнім, щоб повернути системний шрифт.");
    public static string Cancel => Pick("Cancel", "Cancelar", "Отмена", "Скасувати");
    public static string FontNotInstalled => Pick(
        "Not found among installed fonts - Windows will substitute another.",
        "Nao encontrada nas fontes instaladas - o Windows vai usar uma alternativa.",
        "Не найден среди установленных шрифтов — Windows подставит другой.",
        "Не знайдено серед встановлених шрифтів — Windows підставить інший.");
    public static string ButtonsMenu => Pick("Buttons", "Botões", "Кнопки", "Кнопки");
    public static string BtnLike => Pick(
        "Add to favorites (+)", "Adicionar aos favoritos (+)",
        "Добавить в избранное (+)", "Додати до улюблених (+)");
    public static string BtnShuffle => Pick(
        "Shuffle", "Modo aleatório", "Случайный порядок", "Випадковий порядок");
    public static string BtnPlay => Pick(
        "Play/Pause", "Reproduzir/Pausar", "Воспроизведение/Пауза", "Відтворення/Пауза");
    public static string BtnPrev => Pick("Previous", "Anterior", "Предыдущий", "Попередній");
    public static string BtnNext => Pick("Next", "Seguinte", "Следующий", "Наступний");
    public static string BtnRepeat => Pick("Repeat", "Repetição", "Повтор", "Повтор");
    public static string BtnVolume => Pick("Volume", "Volume", "Громкость", "Гучність");
    public static string ShowArt => Pick(
        "Show cover art", "Mostrar capa", "Показывать обложку", "Показувати обкладинку");
    public static string ProgressBar => Pick(
        "Progress bar", "Barra de progresso", "Полоса прогресса", "Смуга прогресу");
    public static string ScrollTitleOnce => Pick(
        "Scroll title only once", "Deslizar título só uma vez",
        "Прокручивать название один раз", "Прокручувати назву один раз");
    public static string AutoSizeText => Pick(
        "Shrink width to fit text", "Largura ajustada ao texto",
        "Ширина по размеру текста", "Ширина за розміром тексту");
    public static string AutoSizeTextTip => Pick(
        "With short titles the widget shrinks and stays tucked against the tray, instead of leaving a gap before the buttons",
        "Com títulos curtos o widget encolhe e encosta-se ao tabuleiro, em vez de deixar um espaço vazio até aos botões",
        "С короткими названиями виджет сжимается и прижимается к трею, вместо пустого места до кнопок",
        "З короткими назвами віджет стискається і притискається до трея, замість порожнього місця до кнопок");
    public static string TextPadding => Pick(
        "Gap after text", "Espaço depois do texto",
        "Отступ после текста", "Відступ після тексту");
    public static string TextPaddingTip => Pick(
        "Gap between the text and the buttons while the width is shrunk to fit",
        "Folga entre o texto e os botões quando a largura está ajustada ao texto",
        "Зазор между текстом и кнопками, когда ширина подстроена под текст",
        "Проміжок між текстом і кнопками, коли ширина підлаштована під текст");
    public static string ShowLauncher => Pick(
        "Show button to open Spotify", "Mostrar botão para abrir o Spotify",
        "Показывать кнопку запуска Spotify", "Показувати кнопку запуску Spotify");
    public static string ShowLauncherTip => Pick(
        "When Spotify is closed, show a button to open it instead of hiding the widget",
        "Com o Spotify fechado, mostra um botão para o abrir em vez de esconder o widget",
        "Когда Spotify закрыт, показывать кнопку запуска вместо скрытия виджета",
        "Коли Spotify закрито, показувати кнопку запуску замість приховування віджета");
    public static string AutoStart => Pick(
        "Start with Windows", "Iniciar com o Windows",
        "Запускать вместе с Windows", "Запускати разом із Windows");
    public static string OpenSpotify => Pick(
        "Open Spotify", "Abrir Spotify", "Открыть Spotify", "Відкрити Spotify");
    public static string CheckUpdates => Pick(
        "Check for updates", "Procurar atualizações",
        "Проверить обновления", "Перевірити оновлення");
    public static string Donate => Pick(
        "Support the project ☕", "Apoiar o projeto ☕",
        "Поддержать проект ☕", "Підтримати проєкт ☕");
    public static string Exit => Pick("Quit", "Sair", "Выход", "Вихід");

    // Tooltips / states
    public static string TipPrev => BtnPrev;
    public static string TipPlayPause => BtnPlay;
    public static string TipNext => BtnNext;
    public static string TipVolume => Pick(
        "Spotify volume", "Volume do Spotify", "Громкость Spotify", "Гучність Spotify");
    public static string TipRepeat => BtnRepeat;
    public static string TipShuffle => BtnShuffle;
    public static string TipShuffleOn => Pick(
        "Shuffle on", "Modo aleatório ativo",
        "Случайный порядок включён", "Випадковий порядок увімкнено");
    public static string TipShuffleSmart => Pick(
        "Smart Shuffle on", "Modo aleatório inteligente ativo",
        "Умный случайный порядок включён", "Розумний випадковий порядок увімкнено");
    public static string TipLikeAdd => Pick(
        "Add to your Spotify favorites", "Adicionar aos favoritos do Spotify",
        "Добавить в избранное Spotify", "Додати до улюблених Spotify");
    public static string TipLiked => Pick(
        "Already in your favorites", "Já está nos favoritos",
        "Уже в избранном", "Вже в улюблених");
    public static string TipLikeUnknown => Pick(
        "Favorites: Spotify only reports this while its app is open (click to add)",
        "Favoritos: o Spotify só reporta o estado com a app aberta (clica para adicionar)",
        "Избранное: Spotify сообщает состояние только при открытом приложении (нажмите, чтобы добавить)",
        "Улюблені: Spotify повідомляє стан лише з відкритим застосунком (натисніть, щоб додати)");
    public static string NothingPlaying => Pick(
        "nothing playing", "nada a tocar", "ничего не играет", "нічого не грає");
    public static string TipOpenSpotify => OpenSpotify;

    // Updates
    public static string UpdateAvailable(Version v) => Pick(
        $"⬤ Update to v{v}", $"⬤ Atualizar para v{v}",
        $"⬤ Обновить до v{v}", $"⬤ Оновити до v{v}");
    public static string UpdateLatest(Version v) => Pick(
        $"You're on the latest version (v{v}).",
        $"Estás na versão mais recente (v{v}).",
        $"У вас последняя версия (v{v}).",
        $"У вас найновіша версія (v{v}).");
    public static string UpdateNotConfigured(Version v) => Pick(
        $"Current version: v{v}\n\nAutomatic updates are turned off in this build.",
        $"Versão atual: v{v}\n\nAs atualizações automáticas estão desligadas nesta build.",
        $"Текущая версия: v{v}\n\nАвтоматические обновления отключены в этой сборке.",
        $"Поточна версія: v{v}\n\nАвтоматичні оновлення вимкнено в цій збірці.");
    public static string UpdatePrompt(Version latest, Version current) => Pick(
        $"New version v{latest} available (current: v{current}).\n\nUpdate now? The widget restarts by itself.",
        $"Nova versão v{latest} disponível (atual: v{current}).\n\nAtualizar agora? O widget reinicia sozinho.",
        $"Доступна новая версия v{latest} (текущая: v{current}).\n\nОбновить сейчас? Виджет перезапустится сам.",
        $"Доступна нова версія v{latest} (поточна: v{current}).\n\nОновити зараз? Віджет перезапуститься сам.");
    public static string UpdateError(string message) => Pick(
        "Could not check for updates: " + message,
        "Não foi possível verificar atualizações: " + message,
        "Не удалось проверить обновления: " + message,
        "Не вдалося перевірити оновлення: " + message);
}
