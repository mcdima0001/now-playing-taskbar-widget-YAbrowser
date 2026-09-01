using System.Windows.Automation;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Locates, through UI Automation, the taskbar elements that anchor the
/// widget: the widgets/weather button (left limit) and the Start button
/// (right limit). Values come back in physical screen pixels.
/// </summary>
internal static class TaskbarAnchors
{
    private static IntPtr _cachedTray;
    private static AutomationElement? _widgetsEl;
    private static AutomationElement? _startEl;

    /// <summary>Сколько чтений подряд не удались. Ссылка на кнопку может
    /// пережить перерисовку виджета погоды и при этом отдавать мусор - без
    /// счётчика виджет оставался с прежним якорем навсегда и выглядел
    /// "съехавшим".</summary>
    private static int _failStreak;

    /// <summary>После стольких неудач подряд (примерно секунда при опросе раз
    /// в 200 мс) кнопки ищутся заново.</summary>
    private const int FailsBeforeRefind = 5;

    /// <summary>Сбросить кеш элементов - после сбоя чтения или смены панели.</summary>
    private static void Invalidate()
    {
        _cachedTray = IntPtr.Zero;
        _widgetsEl = null;
        _startEl = null;
        _failStreak = 0;
    }

    /// <summary>Забыть найденные кнопки и искать заново. Дёргается вручную -
    /// пунктом меню и горячей клавишей, когда виджет всё же встал не там.</summary>
    public static void Reset() => Invalidate();

    /// <summary>Неудачное чтение: пока их немного, кеш бережём (кнопка просто
    /// перерисовывается), но затянувшаяся серия означает мёртвую ссылку.</summary>
    private static (bool, double?, double?, double?) Fail()
    {
        if (++_failStreak >= FailsBeforeRefind) Invalidate();
        return (false, null, null, null);
    }

    /// <summary>Ok=false means the READ failed (UIA threw) - the previous
    /// anchors stay valid. Ok=true with a null value means the element really
    /// does not exist (e.g. widgets turned off). Without this distinction a
    /// transient failure was treated as "the button disappeared" and the
    /// widget landed on top of the weather button.</summary>
    /// <summary>Сами элементы-якоря - на них вешается подписка на изменение
    /// границ. Классический хук WinEvent для них бесполезен: панель Windows 11
    /// нарисована на XAML, отдельных окон у кнопок нет, и LOCATIONCHANGE не
    /// приходит. UIA же присылает уведомление честно.</summary>
    public static (AutomationElement? Widgets, AutomationElement? Start) Buttons(IntPtr tray)
    {
        try
        {
            var root = AutomationElement.FromHandle(tray);
            var widgets = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
            var start = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            return (widgets, start);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Грубые границы кнопок - внешние прямоугольники, без уточнения
    /// по содержимому. Запасной вариант для ручного «поставь на место»: точное
    /// чтение может не удаваться сколько угодно долго, а команда пользователя
    /// обязана сработать сразу, пусть и с небольшой лишней щелью справа от
    /// погоды.</summary>
    public static (double? widgetsRight, double? startLeft) Outer(IntPtr tray)
    {
        try
        {
            var (widgets, start) = Buttons(tray);
            double? wr = null, sl = null;
            if (widgets != null)
            {
                var r = widgets.Current.BoundingRectangle;
                if (!r.IsEmpty) wr = r.Right;
            }
            if (start != null)
            {
                var r = start.Current.BoundingRectangle;
                if (!r.IsEmpty) sl = r.Left;
            }
            return (wr, sl);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <param name="includeTaskButtons">Обход всех кнопок панели стоит дорого и
    /// нужен только правой привязке (лево-выровненные значки). Для обычного
    /// случая его пропускаем, чтобы опрашивать якоря часто и без нагрузки.</param>
    public static (bool Ok, double? widgetsRight, double? startLeft, double? taskButtonsRight) Get(
        IntPtr tray, bool includeTaskButtons = true)
    {
        double? widgetsRight = null, startLeft = null, taskButtonsRight = null;
        try
        {
            // Поиск по всему дереву панели - самая дорогая часть опроса, а
            // кнопки живут ровно столько же, сколько сама панель. Держим ссылки
            // и на каждом проходе читаем только их границы; если элемент умер
            // (перезапуск Explorer), чтение бросит исключение и кеш сбросится
            if (tray != _cachedTray || _widgetsEl == null || _startEl == null)
            {
                var fresh = Buttons(tray);
                _widgetsEl = fresh.Widgets;
                _startEl = fresh.Start;
                _cachedTray = tray;
            }

            var root = AutomationElement.FromHandle(tray);
            var widgets = _widgetsEl;
            if (widgets != null)
            {
                var r = widgets.Current.BoundingRectangle;
                if (!r.IsEmpty) widgetsRight = r.Right;

                // Прямоугольник кнопки заметно шире того, что в ней нарисовано:
                // справа висит пустой отступ (у погоды "31C Sunny" это 190 px
                // кнопки против 107 px содержимого), и виджет вставал с дырой.
                // Берём правый край самого правого текста/иконки внутри - он же
                // сам поедет, когда погода станет длиннее.
                try
                {
                    // Потомков ищем заново каждый раз. Кеш здесь давал сбой:
                    // виджет погоды перерисовывается (то "34°C Sunny", то
                    // "Temps to rise"), старые элементы умирают, чтение по ним
                    // падало - и якорем становился прямоугольник кнопки целиком,
                    // из-за чего виджет отъезжал вправо и больше не подтягивался
                    var found = widgets.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image)));
                    double? contentRight = null;
                    foreach (AutomationElement el in found)
                    {
                        // Элемент может исчезнуть посреди обхода (погода
                        // обновляется) - это не повод терять всю привязку
                        try
                        {
                            var er = el.Current.BoundingRectangle;
                            if (er.IsEmpty) continue;
                            if (contentRight is not double cur || er.Right > cur) contentRight = er.Right;
                        }
                        catch { }
                    }
                    // Доверяем только значению внутри кнопки: чужой элемент или
                    // мусорный прямоугольник не должны утащить виджет
                    if (contentRight is double cr && cr > r.Left && cr < r.Right)
                        widgetsRight = cr;
                    else
                        // Содержимое не прочиталось (кнопка как раз
                        // перерисовывается). Прыгать на её внешний край нельзя -
                        // это видимый скачок; пусть окно оставит прежний якорь
                        return Fail();
                }
                catch
                {
                    Invalidate();
                    return (false, null, null, null);
                }
            }

            var start = _startEl;
            if (start != null)
            {
                var r = start.Current.BoundingRectangle;
                if (!r.IsEmpty) startLeft = r.Left;
            }

            // End of the app icon row (so they are not covered when the widget
            // anchors right, on left-aligned / secondary taskbars)
            if (!includeTaskButtons)
            {
                _failStreak = 0;
                return (true, widgetsRight, startLeft, null);
            }

            var buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                // A button can die MID-enumeration (apps opening/closing);
                // without the per-button try, throwing here discarded the whole
                // read - including the anchors already read successfully above
                try
                {
                    string cls = button.Current.ClassName ?? "";
                    if (!cls.StartsWith("Taskbar.TaskListButton", StringComparison.Ordinal)) continue;
                    var r = button.Current.BoundingRectangle;
                    if (r.IsEmpty) continue;
                    if (taskButtonsRight is not double cur || r.Right > cur)
                        taskButtonsRight = r.Right;
                }
                catch { }
            }
        }
        catch
        {
            // Чаще всего это умерший элемент после перезапуска Explorer -
            // сбрасываем кеш, следующий проход найдёт кнопки заново
            Invalidate();
            return (false, null, null, null);
        }
        _failStreak = 0;
        return (true, widgetsRight, startLeft, taskButtonsRight);
    }
}
