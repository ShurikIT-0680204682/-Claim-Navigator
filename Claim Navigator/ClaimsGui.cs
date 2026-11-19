using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Claim_Navigator
{
    public class ClaimsGui : GuiDialog
    {
        private const string ScrollbarKey = "privatescrollbar";

        private const int RowHeight = 30;

        private const int RowGap = 10;

        private const int MembersPerPage = 8;

        // Пауза між командами, мс. Сервер відхиляє команди частіше ніж раз на секунду
        private const int CommandDelayMs = 1500;

        // Скільки разів відправляти одну команду, якщо сервер відповів на неї помилкою (CommandError).
        // 1 — без повторів: при паузі 1,5 с відмови через частоту від мода не буває, а справжня помилка
        // при повторі просто прийшла б ще раз
        private const int MaxAttempts = 1;

        // Скільки чекати відповіді сервера на команду, мс. Не дочекалися — вважаємо, що виконана
        private const int ReplyTimeoutMs = 6000;

        // Префікс необов'язкової команди: її помилка не зупиняє чергу і не повторюється.
        // Наприклад, "/land claim add" перед збереженням, коли область уже могла бути додана
        private const string OptionalPrefix = "?";

        private static readonly double[] PlaceholderColor = new double[4] { 0.75, 0.62, 0.5, 1.0 };

        private readonly Claim_Navigator mod;

        // Вибраний приват (об'єкт з клієнтського списку приватів)
        private LandClaim selected;

        // Області вибраного привату: після збереження сервер перестворює приват, за ними він знаходиться заново
        private List<int[]> selectedAreas;

        private readonly Dictionary<string, string> fields = new Dictionary<string, string>();

        // Змінні для списку приватів
        private List<LandClaim> ownClaims = new List<LandClaim>();

        private ElementBounds listContentBounds;

        private GuiElementContainer listContainer;

        private int listTotalHeight;

        private int listMaxHeight;

        private string searchText = "";

        public override string ToggleKeyCombinationCode => null;

        public ClaimsGui(ICoreClientAPI capi, Claim_Navigator mod)
            : base(capi)
        {
            this.mod = mod;
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            EditSession session = mod.Session;
            if (session != null)
            {
                // Повертаємось до незавершеного створення/розширення, нічого не перезавантажуючи на сервері
                if (session.IsNew)
                {
                    EditMenu(isNew: true);
                    return;
                }
                selectedAreas = session.Areas;
                if (ResolveSelected() >= 0)
                {
                    EditMenu(isNew: false);
                    return;
                }
                mod.Session = null;
            }
            BuildListMenu();
        }

        // ===================== ДАНІ =====================

        private static string L(string key, params object[] args)
        {
            return Lang.Get("claimnavigator:" + key, args);
        }

        // Привати гравця в тому ж порядку, що й на сервері: індекс у цьому списку —
        // це id для /land claim load і /land free
        private List<LandClaim> OwnClaims()
        {
            string uid = capi.World.Player.PlayerUID;
            List<LandClaim> all = capi.World.Claims?.All;
            if (all == null)
            {
                return new List<LandClaim>();
            }
            return all.Where((LandClaim c) => c != null && c.OwnedByPlayerUid == uid).ToList();
        }

        private void Select(LandClaim claim)
        {
            selected = claim;
            selectedAreas = claim.Areas.Select((Cuboidi a) => new int[6] { a.X1, a.Y1, a.Z1, a.X2, a.Y2, a.Z2 }).ToList();
        }

        private static bool SameArea(int[] a, Cuboidi b)
        {
            return a[0] == b.X1 && a[1] == b.Y1 && a[2] == b.Z1 && a[3] == b.X2 && a[4] == b.Y2 && a[5] == b.Z2;
        }

        // Актуальний серверний індекс вибраного привату або -1. Після будь-якого збереження сервер
        // переносить приват у кінець списку, тому індекс не можна запам'ятовувати — лише шукати заново
        private int ResolveSelected()
        {
            List<LandClaim> own = OwnClaims();
            int index = -1;
            if (selectedAreas != null && selectedAreas.Count > 0)
            {
                // З кінця: збережений приват сервер додає в кінець списку
                index = own.FindLastIndex((LandClaim c) => selectedAreas.All((int[] a) => c.Areas.Any((Cuboidi b) => SameArea(a, b))));
            }
            if (index < 0 && selected != null)
            {
                index = own.IndexOf(selected);
            }
            if (index >= 0)
            {
                Select(own[index]);
            }
            return index;
        }

        private string ClaimName(LandClaim claim)
        {
            return string.IsNullOrWhiteSpace(claim?.Description) ? L("unnamed") : claim.Description.Trim();
        }

        private static string SaveName(LandClaim claim)
        {
            return (claim?.Description ?? "").Trim();
        }

        // Рівень доступу в тому вигляді, як його приймає /land claim grant
        private static string LevelOf(EnumBlockAccessFlags flags)
        {
            if ((flags & EnumBlockAccessFlags.BuildOrBreak) != EnumBlockAccessFlags.None)
            {
                return "all";
            }
            return ((flags & EnumBlockAccessFlags.Use) != EnumBlockAccessFlags.None) ? "use" : "traverse";
        }

        private string PermText(EnumBlockAccessFlags flags)
        {
            if ((flags & EnumBlockAccessFlags.BuildOrBreak) != EnumBlockAccessFlags.None)
            {
                return L("perm-all");
            }
            if ((flags & EnumBlockAccessFlags.Use) != EnumBlockAccessFlags.None)
            {
                return L("perm-use");
            }
            return L("perm-traverse");
        }

        // ===================== КОМАНДИ =====================

        // Відправляє команди по черзі й після кожної чекає відповідь сервера саме на неї.
        // Сервер відповідає на кожну команду рівно одним повідомленням, по порядку: CommandSuccess —
        // виконана, CommandError — не виконана (відмова через частоту або справжня помилка).
        // Невиконану команду повторюємо; якщо сервер знову відповідає помилкою — зупиняємо чергу.
        // done викликається, лише якщо все виконано і сервер встиг розіслати оновлені привати;
        // onSuccess — теж лише при успіху, але навіть якщо вікно вже закрите
        private bool Run(List<string> commands, Action done = null, LandClaim watch = null, Action onSuccess = null)
        {
            List<LandClaim> listBefore = capi.World.Claims?.All;
            if (mod.Busy)
            {
                capi.ShowChatMessage(L("busy"));
                return false;
            }
            if (commands.Count == 0)
            {
                onSuccess?.Invoke();
                done?.Invoke();
                return true;
            }
            mod.Busy = true;
            int i = 0;
            int attempts = 0;
            long sentAt = 0;
            // Хто чекає наступну відповідь сервера: номер нашої команди або -1 для команди, набраної гравцем
            // у чаті, поки йде черга (її відповідь теж прийде, і її треба пропустити)
            Queue<int> expected = new Queue<int>();
            int replyFor = -1;
            bool replyOk = false;
            string replyText = null;
            ChatLineDelegate onChat = delegate(int groupId, string message, EnumChatType chatType, string data)
            {
                if ((chatType != EnumChatType.CommandSuccess && chatType != EnumChatType.CommandError) || expected.Count == 0)
                {
                    return;
                }
                int index = expected.Dequeue();
                if (index >= 0)
                {
                    replyFor = index;
                    replyOk = chatType == EnumChatType.CommandSuccess;
                    replyText = message;
                }
            };
            ClientChatLineDelegate onTyped = delegate(int groupId, ref string message, ref EnumHandling handled)
            {
                if (message != null && message.StartsWith("/"))
                {
                    expected.Enqueue(-1);
                }
            };
            capi.Event.ChatMessage += onChat;
            capi.Event.OnSendChatMessage += onTyped;
            Action<float> step = null;
            Action<float> check = null;
            Action finish = null;
            step = delegate
            {
                if (i >= commands.Count)
                {
                    finish();
                    return;
                }
                // Пауза рахується від останньої відправленої модом команди, у якому б вікні й меню
                // вона не була: якщо з неї минуло менше 1,5 с, чекаємо залишок
                long sinceLast = capi.ElapsedMilliseconds - mod.LastCommandMs;
                if (mod.LastCommandMs > 0 && sinceLast < CommandDelayMs)
                {
                    capi.Event.RegisterCallback(step, (int)(CommandDelayMs - sinceLast));
                    return;
                }
                replyFor = -1;
                expected.Enqueue(i);
                capi.SendChatMessage(CommandText(commands[i]));
                mod.LastCommandMs = sentAt = capi.ElapsedMilliseconds;
                attempts++;
                capi.Event.RegisterCallback(check, CommandDelayMs);
            };
            check = delegate
            {
                bool optional = commands[i].StartsWith(OptionalPrefix);
                if (replyFor != i)
                {
                    // Відповіді ще немає: почекаємо, але не нескінченно — без відповіді вважаємо виконаною
                    if (capi.ElapsedMilliseconds - sentAt < ReplyTimeoutMs)
                    {
                        capi.Event.RegisterCallback(check, 250);
                        return;
                    }
                }
                else if (!replyOk && !optional)
                {
                    if (attempts < MaxAttempts)
                    {
                        step(0f);
                        return;
                    }
                    capi.Event.ChatMessage -= onChat;
                    capi.Event.OnSendChatMessage -= onTyped;
                    mod.Busy = false;
                    capi.ShowChatMessage(L("command-failed", CommandText(commands[i]), replyText));
                    return;
                }
                i++;
                attempts = 0;
                step(0f);
            };
            finish = delegate
            {
                capi.Event.ChatMessage -= onChat;
                capi.Event.OnSendChatMessage -= onTyped;
                onSuccess?.Invoke();
                if (done == null)
                {
                    mod.Busy = false;
                    return;
                }
                // Не перемальовуємо вікно, поки сервер не надіслав змінений приват: при збереженні
                // він перестворює приват, тому старий об'єкт зникає з клієнтського списку
                int waited = 0;
                Action<float> poll = null;
                poll = delegate
                {
                    List<LandClaim> now = capi.World.Claims?.All;
                    bool changed = watch == null || now == null || now != listBefore || !now.Contains(watch);
                    if (!changed && waited < 8000)
                    {
                        waited += 250;
                        capi.Event.RegisterCallback(poll, 250);
                        return;
                    }
                    // Оновлення приходить кількома пакетами поспіль — даємо дійти останньому
                    capi.Event.RegisterCallback(delegate
                    {
                        mod.Busy = false;
                        if (IsOpened())
                        {
                            mod.RefreshNames();
                            done();
                        }
                    }, 400);
                };
                poll(0f);
            };
            step(0f);
            return true;
        }

        // Текст команди для сервера: без позначки необов'язковості
        private static string CommandText(string command)
        {
            return command.StartsWith(OptionalPrefix) ? command.Substring(OptionalPrefix.Length) : command;
        }

        // Додає збереження. /land claim load на сервері скидає "доступ для всіх",
        // тому перед збереженням повертаємо ці прапорці такими, якими вони були
        private static void AppendSave(List<string> commands, LandClaim claim, string name)
        {
            if (claim != null)
            {
                if (claim.AllowUseEveryone && !commands.Any((string c) => c.StartsWith("/land claim allowuseeveryone")))
                {
                    commands.Add("/land claim allowuseeveryone true");
                }
                if (claim.AllowTraverseEveryone && !commands.Any((string c) => c.StartsWith("/land claim allowtraverseveryone")))
                {
                    commands.Add("/land claim allowtraverseveryone true");
                }
            }
            commands.Add("/land claim save " + (name ?? "").Trim());
        }

        // Завантажити приват → виконати команди → зберегти під тим самим ім'ям
        private void RunEdit(Action done, params string[] middle)
        {
            int index = ResolveSelected();
            if (index < 0)
            {
                capi.ShowChatMessage(L("claim-not-found"));
                BuildListMenu();
                return;
            }
            List<string> commands = new List<string> { $"/land claim load {index}" };
            commands.AddRange(middle);
            AppendSave(commands, selected, SaveName(selected));
            Run(commands, done, selected);
        }

        // Після зміни: заново знайти приват і перемалювати екран
        private void Reopen(Action screen)
        {
            if (ResolveSelected() >= 0)
            {
                screen();
            }
            else
            {
                BuildListMenu();
            }
        }

        // ===================== ВЕРСТКА =====================

        private GuiComposer Begin(string key, string title, int innerWidth, int rows)
        {
            // Перший рядок — на RowGap нижче заголовка (31), як і проміжок між кнопками; знизу такий самий відступ
            int height = 41 + rows * (RowHeight + RowGap);
            ElementBounds dialogBounds = ElementBounds.Fixed(0.0, 0.0, innerWidth + 40, height).WithAlignment(EnumDialogArea.CenterMiddle).WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);
            ElementBounds innerBounds = ElementBounds.Fixed(0.0, 21.0, innerWidth, height - 31).WithFixedPadding(10.0, 10.0).WithSizing(ElementSizing.FitToChildren);
            fields.Clear();
            return capi.Gui.CreateCompo(key, dialogBounds).AddShadedDialogBG(ElementBounds.Fill).AddDialogBG(ElementBounds.Fill, withTitleBar: false)
                .AddDialogTitleBar(title, delegate
                {
                    TryClose();
                })
                .BeginChildElements(innerBounds);
        }

        private static ElementBounds Row(ref int y, int innerWidth)
        {
            // Діалог ширший на 40: відступ 20 зліва (10 padding + 10 x) і 20 справа
            ElementBounds bounds = ElementBounds.Fixed(10.0, y, innerWidth, RowHeight);
            y += RowHeight + RowGap;
            return bounds;
        }

        // Запас по боках напису на кнопці й межа ширини імені учасника (довше — обріжеться)
        private const int ButtonTextPad = 24;
        private const int MaxMemberLabelWidth = 320;

        // Скільки місця під рядок учасника є завжди, навіть якщо кнопки внизу вузькі
        private const int MinMemberLabelWidth = 200;

        // Проміжок між кнопками в одному рядку (сітка у вікні «Учасники»)
        private const int GridGap = 10;

        // Ширина тексту в немасштабованих одиницях інтерфейсу
        private static int TextWidth(CairoFont font, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            return (int)Math.Ceiling(font.GetTextExtents(text).XAdvance / RuntimeEnv.GUIScale);
        }

        // Текст, обрізаний з трикрапкою так, щоб вліз у maxWidth, — замість перенесення на другий рядок
        private static string Fit(CairoFont font, string text, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || TextWidth(font, text) <= maxWidth)
            {
                return text;
            }
            for (int length = text.Length - 1; length > 0; length--)
            {
                string cut = text.Substring(0, length).TrimEnd() + "…";
                if (TextWidth(font, cut) <= maxWidth)
                {
                    return cut;
                }
            }
            return "…";
        }

        // Рядок учасника «ім'я — рівень», не ширший за maxWidth: якщо не влазить, скорочується
        // лише ім'я (з трикрапкою), а рівень доступу лишається видно повністю
        private static string FitName(string key, string name, string level, int maxWidth)
        {
            CairoFont font = CairoFont.WhiteSmallText();
            string label = L(key, name, level);
            if (TextWidth(font, label) <= maxWidth)
            {
                return label;
            }
            for (int length = name.Length - 1; length > 0; length--)
            {
                label = L(key, name.Substring(0, length).TrimEnd() + "…", level);
                if (TextWidth(font, label) <= maxWidth)
                {
                    return label;
                }
            }
            return Fit(font, L(key, "…", level), maxWidth);
        }

        // Початок UID для показу (UID гравця — 24 символи й не влазить у рядок).
        // Повний UID — у підказці при наведенні, команди відправляються з повним
        private static string ShortUid(string uid)
        {
            return (uid.Length > 8) ? (uid.Substring(0, 8) + "…") : uid;
        }

        // Рядок із кнопкою заданої ширини по центру вікна
        private static ElementBounds CenteredRow(ref int y, int innerWidth, int buttonWidth)
        {
            buttonWidth = Math.Min(buttonWidth, innerWidth);
            ElementBounds bounds = ElementBounds.Fixed(10 + (innerWidth - buttonWidth) / 2, y, buttonWidth, RowHeight);
            y += RowHeight + RowGap;
            return bounds;
        }

        private void AddButton(GuiComposer composer, ElementBounds bounds, string text, string hint, Action onClick)
        {
            composer.AddSmallButton(text, delegate
            {
                onClick();
                return true;
            }, bounds);
            if (hint != null)
            {
                composer.AddAutoSizeHoverText(hint, CairoFont.WhiteDetailText(), 250, bounds);
            }
        }

        // centered — підказка по центру поля, для вузьких полів на кшталт «Північ»/«Захід»
        private void AddField(GuiComposer composer, ElementBounds bounds, string key, string placeholder, bool centered = false)
        {
            composer.AddTextInput(bounds, delegate(string text)
            {
                fields[key] = text ?? "";
                base.SingleComposer?.GetDynamicText("ph_" + key)?.SetNewText(string.IsNullOrEmpty(text) ? placeholder : "");
            }, CairoFont.TextInput(), key);
            CairoFont font = CairoFont.WhiteSmallText().WithColor(PlaceholderColor);
            ElementBounds placeholderBounds = bounds.FlatCopy().WithFixedOffset(5.0, 5.0);
            if (centered)
            {
                font = font.WithOrientation(EnumTextOrientation.Center);
                placeholderBounds = bounds.FlatCopy().WithFixedOffset(0.0, 5.0);
            }
            composer.AddDynamicText(placeholder, font, placeholderBounds, "ph_" + key);
        }

        private string Field(string key)
        {
            fields.TryGetValue(key, out string value);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = base.SingleComposer?.GetTextInput(key)?.GetText() ?? "";
            }
            return value.Trim();
        }

        private int NumberField(string key)
        {
            return int.TryParse(Field(key), out int value) ? value : 0;
        }

        // ===================== ГОЛОВНЕ МЕНЮ СПИСКУ ПРИВАТІВ =====================

        private void OnScrollChanged(float value)
        {
            int hidden = Math.Max(0, listTotalHeight - listMaxHeight);
            // Коли все вміщується, скролбар ділить 0 на 0 і надсилає NaN
            if (float.IsNaN(value) || float.IsInfinity(value) || hidden == 0)
            {
                value = 0f;
            }
            float offset = Math.Clamp((value > 1f) ? value : (value * (float)hidden), 0f, hidden);
            if (listContentBounds != null)
            {
                listContentBounds.fixedY = -(int)offset;
                listContentBounds.CalcWorldBounds();
            }
        }

        // Розмітка списку (у немасштабованих одиницях, від лівого верхнього кута вікна):
        // відступ 20 з боків, пошук під заголовком, нижче список тієї ж ширини, що прокручується,
        // скролбар — одразу праворуч від кнопок і лише якщо привати не вміщуються
        private const int ListPad = 20;
        private const int ListTileWidth = 300;
        private const int ListRowGap = 5;
        private const int ListVisibleRows = 11;
        private const int ListSearchY = 41;
        private const int ListTop = ListSearchY + RowHeight + 10;
        private const int ListScrollbarGap = 6;
        private const int ListScrollbarWidth = 10;

        private HideableScrollbar listScrollbar;

        private static int ListHeightFor(int rows)
        {
            // Рядок «Створити приват» + привати, без проміжку в кінці
            return (rows + 1) * (RowHeight + ListRowGap) - ListRowGap;
        }

        private void BuildListMenu()
        {
            ownClaims = OwnClaims();
            searchText = "";
            fields.Clear();
            // Висота вікна — за кількістю приватів, але не більше ListVisibleRows рядків; без приватів — лише «Створити приват»
            listMaxHeight = ListHeightFor(Math.Min(ownClaims.Count, ListVisibleRows - 1));
            listTotalHeight = ListHeightFor(ownClaims.Count);
            // Ширина вікна залежить від того, чи потрібен скрол для повного списку: без нього вікно вужче
            bool canScroll = listTotalHeight > listMaxHeight;
            int dialogWidth = ListPad + ListTileWidth + (canScroll ? ListScrollbarGap + ListScrollbarWidth : 0) + ListPad;
            ElementBounds dialogBounds = ElementBounds.Fixed(0.0, 0.0, dialogWidth, ListTop + listMaxHeight + RowGap).WithAlignment(EnumDialogArea.CenterMiddle).WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);
            GuiComposer composer = capi.Gui.CreateCompo("privatesdialog", dialogBounds).AddShadedDialogBG(ElementBounds.Fill).AddDialogBG(ElementBounds.Fill, withTitleBar: false)
                .AddDialogTitleBar(L("title-buildlistmenu"), delegate
                {
                    TryClose();
                });
            // Пошук тягнеться до правого краю скролбара, якщо він є
            ElementBounds searchBounds = ElementBounds.Fixed(ListPad, ListSearchY, dialogWidth - 2 * ListPad, RowHeight);
            composer.AddTextInput(searchBounds, delegate(string text)
            {
                searchText = text ?? "";
                FillList(recompose: true);
                base.SingleComposer?.GetDynamicText("ph_search")?.SetNewText(string.IsNullOrEmpty(searchText) ? L("text-search") : "");
            }, CairoFont.WhiteSmallText(), "searchInput");
            composer.AddDynamicText(L("text-search"), CairoFont.WhiteSmallText().WithColor(PlaceholderColor), searchBounds.FlatCopy().WithFixedOffset(5.0, 5.0), "ph_search");
            listScrollbar = null;
            if (canScroll)
            {
                listScrollbar = new HideableScrollbar(capi, OnScrollChanged, ElementBounds.Fixed(ListPad + ListTileWidth + ListScrollbarGap, ListTop, ListScrollbarWidth, listMaxHeight));
                composer.AddInteractiveElement(listScrollbar, ScrollbarKey);
            }
            composer.BeginClip(ElementBounds.Fixed(ListPad, ListTop, ListTileWidth, listMaxHeight))
                .BeginChildElements(listContentBounds = ElementBounds.Fixed(0.0, 0.0, ListTileWidth, listTotalHeight));
            listContainer = new GuiElementContainer(capi, ElementBounds.Fixed(0.0, 0.0, ListTileWidth, listTotalHeight));
            // «Створити приват» лежить у тому ж контейнері, що й кнопки приватів: контейнер перехоплює кліки
            // по всій своїй площі, і окрема кнопка під ним не натискалася б
            composer.AddInteractiveElement(listContainer, "listContainer");
            FillList(recompose: false);
            composer.EndChildElements();
            composer.EndClip();
            base.SingleComposer = composer.Compose();
            UpdateListScroll();
        }

        // Висоти для скролбара, його видимість і повернення списку на початок
        private void UpdateListScroll()
        {
            if (listScrollbar != null)
            {
                listScrollbar.Hidden = listTotalHeight <= listMaxHeight;
                listScrollbar.SetHeights(listMaxHeight, Math.Max(listMaxHeight, listTotalHeight));
                listScrollbar.CurrentYPosition = 0f;
            }
            OnScrollChanged(0f);
        }

        private void FillList(bool recompose)
        {
            if (listContainer == null)
            {
                return;
            }
            listContainer.Clear();
            listContainer.Add(new GuiElementTextButton(capi, L("title-newclaimmenu"), CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold), delegate
            {
                EditMenu(isNew: true);
                return true;
            }, ElementBounds.Fixed(0.0, 0.0, ListTileWidth, RowHeight)));
            ElementBounds bounds = ElementBounds.Fixed(0.0, RowHeight + ListRowGap, ListTileWidth, RowHeight);
            int shown = 0;
            for (int i = 0; i < ownClaims.Count; i++)
            {
                LandClaim claim = ownClaims[i];
                string name = ClaimName(claim);
                if (!string.IsNullOrEmpty(searchText) && name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                GuiElementTextButton button = new GuiElementTextButton(capi, $"{i + 1}. {name}", CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText(), delegate
                {
                    Select(claim);
                    Reopen(delegate
                    {
                        ActionMenu();
                    });
                    return true;
                }, bounds);
                listContainer.Add(button);
                bounds = bounds.BelowCopy(0.0, ListRowGap);
                shown++;
            }
            listTotalHeight = ListHeightFor(shown);
            if (listContentBounds != null)
            {
                listContentBounds.fixedHeight = Math.Max(listMaxHeight, listTotalHeight);
            }
            listContainer.Bounds.fixedHeight = Math.Max(listMaxHeight, listTotalHeight);
            listContainer.Bounds.CalcWorldBounds();
            if (!recompose || base.SingleComposer == null)
            {
                return;
            }
            UpdateListScroll();
            base.SingleComposer.ReCompose();
        }

        // ===================== ДІЇ З ПРИВАТОМ =====================

        private void ActionMenu(bool confirmDelete = false)
        {
            int width = 300;
            LandClaim claim = selected;
            string name = ClaimName(claim);
            // Гравці, групи та загальний доступ — усе це тепер у вікні «Учасники»
            int members = GetMembers(claim).Count;
            GuiComposer composer = Begin("privatedetails", L("title-actionmenu", name), width, 7);
            int y = 10;
            AddButton(composer, Row(ref y, width), L("back"), null, BuildListMenu);
            AddButton(composer, Row(ref y, width), L("allocate-claim"), L("hint-allocate-claim"), delegate
            {
                int index = ResolveSelected();
                if (index >= 0 && Run(new List<string> { $"/land claim load {index}" }))
                {
                    TryClose();
                }
            });
            AddButton(composer, Row(ref y, width), L("cancel-claim"), L("hint-cancel-claim"), delegate
            {
                if (Run(new List<string> { OptionalPrefix + "/land claim cancel" }))
                {
                    mod.Session = null;
                    TryClose();
                }
            });
            AddButton(composer, Row(ref y, width), L("expand-claim"), L("hint-expand-claim"), StartExpand);
            AddButton(composer, Row(ref y, width), L("rename-claim"), L("hint-rename-claim"), RenameMenu);
            AddButton(composer, Row(ref y, width), L("members-claim", members), L("hint-members-claim"), delegate
            {
                MembersMenu();
            });
            AddButton(composer, Row(ref y, width), confirmDelete ? L("delete-claim-confirm") : L("delete-claim"), L("hint-delete-claim"), delegate
            {
                if (!confirmDelete)
                {
                    ActionMenu(confirmDelete: true);
                    return;
                }
                int index = ResolveSelected();
                if (index >= 0)
                {
                    Run(new List<string>
                    {
                        $"/land free {index}",
                        $"/land free {index} confirm"
                    }, BuildListMenu, selected);
                }
            });
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
        }

        // ===================== УЧАСНИКИ ПРИВАТУ =====================

        private class Member
        {
            // Текст рядка під задану ширину: «ім'я — рівень», коли бракує місця, скорочується ім'я
            public System.Func<int, string> Text;

            public string Hover;

            // Команди для виключення; null — виключити командою не можна (група з невідомою назвою)
            public string[] RevokeCommands;

            public string Name;

            // Команда, після якої сервер повідомить ім'я гравця; null — ім'я вже відоме
            public string RenameCommand;

            public EnumBlockAccessFlags Flags;

            // Команда видачі доступу, {0} — use / traverse / all; null — змінити доступ командою не можна
            public string GrantFormat;

            // Своє меню для кнопки «Доступ» замість вибору рівня (загальний доступ)
            public Action<int> AccessMenu;
        }

        // Текст загального доступу привату або null, якщо він вимкнений
        private static string EveryoneText(LandClaim claim)
        {
            if (claim.AllowUseEveryone && claim.AllowTraverseEveryone)
            {
                return L("everyone-both");
            }
            if (claim.AllowUseEveryone)
            {
                return L("perm-use");
            }
            return claim.AllowTraverseEveryone ? L("perm-traverse") : null;
        }

        private List<Member> GetMembers(LandClaim claim)
        {
            List<Member> members = new List<Member>();
            // Загальний доступ показуємо як учасника «Усі гравці», першим у списку
            string everyone = EveryoneText(claim);
            if (everyone != null)
            {
                List<string> revoke = new List<string>();
                if (claim.AllowUseEveryone)
                {
                    revoke.Add("/land claim allowuseeveryone false");
                }
                if (claim.AllowTraverseEveryone)
                {
                    revoke.Add("/land claim allowtraverseveryone false");
                }
                members.Add(new Member
                {
                    Text = (int w) => Fit(CairoFont.WhiteSmallText(), L("member-everyone", everyone), w),
                    Hover = L("hint-member-everyone"),
                    RevokeCommands = revoke.ToArray(),
                    AccessMenu = SubmenuGeneralAccess
                });
            }
            foreach (KeyValuePair<string, EnumBlockAccessFlags> pair in claim.PermittedPlayerUids)
            {
                if (pair.Value == EnumBlockAccessFlags.None)
                {
                    continue;
                }
                string uid = pair.Key;
                string playerName = mod.NameFor(uid, claim);
            string level = PermText(pair.Value);
                members.Add(new Member
                {
                    Text = (int w) => FitName("member-player", playerName ?? ("UID " + ShortUid(uid)), level, w),
                    Hover = ((playerName != null) ? (playerName + "\n") : "") + "UID: " + uid,
                    // За UID сервер знаходить гравця, навіть якщо той змінив нік
                    RevokeCommands = new string[1] { "/land claim revoke " + uid },
                    Name = playerName ?? ("UID " + ShortUid(uid)),
                    // Повторна видача того самого рівня нічого не змінює, але сервер записує в приват ім'я гравця
                    RenameCommand = ((playerName == null) ? ("/land claim grant " + uid + " " + LevelOf(pair.Value)) : null),
                    Flags = pair.Value,
                    GrantFormat = "/land claim grant " + uid + " {0}"
                });
            }
            foreach (KeyValuePair<int, EnumBlockAccessFlags> pair in claim.PermittedPlayerGroupIds)
            {
                if (pair.Value == EnumBlockAccessFlags.None)
                {
                    continue;
                }
                // Назву групи клієнт знає, лише якщо сам у ній перебуває
                string groupName = capi.World.Player.GetGroup(pair.Key)?.GroupName;
            string level = PermText(pair.Value);
                members.Add(new Member
                {
                    Text = (int w) => FitName("member-group", groupName ?? ("#" + pair.Key), level, w),
                    Hover = ((groupName == null) ? L("hint-member-group-unknown") : (groupName + "\nID: " + pair.Key)),
                    RevokeCommands = ((groupName == null) ? null : new string[1] { "/land claim revokegroup " + groupName }),
                    Name = L("member-group-name", groupName ?? ("#" + pair.Key)),
                    Flags = pair.Value,
                    GrantFormat = ((groupName == null) ? null : ("/land claim grantgroup " + groupName + " {0}"))
                });
            }
            return members;
        }

        private void MembersMenu(int page = 0, bool confirmAll = false)
        {
            LandClaim claim = selected;
            List<Member> members = GetMembers(claim);
            int pages = Math.Max(1, (members.Count + MembersPerPage - 1) / MembersPerPage);
            page = GameMath.Clamp(page, 0, pages - 1);
            List<Member> shown = members.Skip(page * MembersPerPage).Take(MembersPerPage).ToList();
            List<string> revokeAll = members.Where((Member m) => m.RevokeCommands != null).SelectMany((Member m) => m.RevokeCommands).ToList();
            int revokeCount = members.Count((Member m) => m.RevokeCommands != null);
            List<string> renameAll = members.Where((Member m) => m.RenameCommand != null).Select((Member m) => m.RenameCommand).ToList();
            CairoFont textFont = CairoFont.WhiteSmallText();
            CairoFont buttonFont = CairoFont.SmallButtonText();
            string countText = (members.Count == 0) ? L("members-empty") : L("members-count", members.Count);
            string removeAllText = confirmAll ? L("members-remove-all-confirm", revokeCount) : L("members-remove-all");
            int accessWidth = TextWidth(buttonFont, L("member-access")) + ButtonTextPad;
            int removeWidth = TextWidth(buttonFont, L("member-remove")) + ButtonTextPad;
            // Нижні кнопки — сіткою 2×2: «Додати гравця» | «Додати групу», «Загальний доступ» | «Видалити всіх».
            // Ширина комірки — за найдовшим написом, у «Видалити всіх» одразу за обома його варіантами,
            // щоб вікно не змінювало ширину після першого натискання
            List<string> gridTexts = new List<string>
            {
                L("members-add-player"),
                L("members-add-group"),
                L("general-claim")
            };
            if (revokeAll.Count > 0)
            {
                gridTexts.Add(L("members-remove-all"));
                gridTexts.Add(L("members-remove-all-confirm", revokeCount));
            }
            int cellWidth = gridTexts.Max((string t) => TextWidth(buttonFont, t)) + ButtonTextPad;
            int gridWidth = cellWidth * 2 + GridGap;
            List<string> wideTexts = new List<string> { L("back") };
            if (renameAll.Count > 0)
            {
                wideTexts.Add(L("members-resolve-names", renameAll.Count));
            }
            // «Назад» і «Дізнатися імена» — на всю ширину сітки
            int wideButtonWidth = Math.Max(wideTexts.Max((string t) => TextWidth(buttonFont, t)) + ButtonTextPad * 2, gridWidth);
            // Ширина вікна — за кнопками й написом зверху, а не за іменами на сторінці: так вона одна на всіх
            // сторінках. Рядки учасників (ім'я — рівень) підрізаються під цю ширину, рівень завжди видно
            int rowButtonsWidth = 15 + accessWidth + 5 + removeWidth;
            int width = Math.Max(Math.Max(wideButtonWidth, TextWidth(textFont, countText)), Math.Max(240, (members.Count > 0) ? (MinMemberLabelWidth + rowButtonsWidth) : 0));
            int labelWidth = Math.Min(MaxMemberLabelWidth, width - rowButtonsWidth);
            // Назад, лічильник, учасники, сторінки, «дізнатися імена», два рядки сітки
            int rows = 2 + shown.Count + ((pages > 1) ? 1 : 0) + ((renameAll.Count > 0) ? 1 : 0) + 2;
            GuiComposer composer = Begin("claimmembers", L("title-members", ClaimName(claim)), width, rows);
            int y = 10;
            AddButton(composer, CenteredRow(ref y, width, wideButtonWidth), L("back"), null, delegate
            {
                ActionMenu();
            });
            // Без учасників під написом немає рядків з іменами — тоді ставимо його по центру, як кнопки
            EnumTextOrientation infoOrientation = (shown.Count == 0) ? EnumTextOrientation.Center : EnumTextOrientation.Left;
            composer.AddStaticText(countText, textFont, infoOrientation, Row(ref y, width).WithFixedOffset(0.0, 7.0));
            foreach (Member member in shown)
            {
                ElementBounds textBounds = ElementBounds.Fixed(10.0, y + 7, width - accessWidth - removeWidth - 10, RowHeight - 7);
                ElementBounds accessBounds = ElementBounds.Fixed(10 + width - removeWidth - 5 - accessWidth, y, accessWidth, RowHeight);
                ElementBounds buttonBounds = ElementBounds.Fixed(10 + width - removeWidth, y, removeWidth, RowHeight);
                if (member.GrantFormat != null || member.AccessMenu != null)
                {
                    Member target = member;
                    int returnPage = page;
                    AddButton(composer, accessBounds, L("member-access"), L("hint-member-access"), delegate
                    {
                        if (target.AccessMenu != null)
                        {
                            target.AccessMenu(returnPage);
                        }
                        else
                        {
                            MemberAccessMenu(target, returnPage);
                        }
                    });
                }
                y += RowHeight + RowGap;
                composer.AddStaticText(member.Text(labelWidth), textFont, textBounds);
                composer.AddAutoSizeHoverText(member.Hover, CairoFont.WhiteDetailText(), 400, textBounds);
                if (member.RevokeCommands != null)
                {
                    string[] command = member.RevokeCommands;
                    int currentPage = page;
                    AddButton(composer, buttonBounds, L("member-remove"), L("hint-member-remove"), delegate
                    {
                        RunEdit(delegate
                        {
                            Reopen(delegate
                            {
                                MembersMenu(currentPage);
                            });
                        }, command);
                    });
                }
            }
            if (pages > 1)
            {
                int currentPage = page;
                ElementBounds prevBounds = ElementBounds.Fixed(10.0, y, 60.0, RowHeight);
                ElementBounds pageBounds = ElementBounds.Fixed(80.0, y + 7, width - 140, RowHeight - 7);
                ElementBounds nextBounds = ElementBounds.Fixed(10 + width - 60, y, 60.0, RowHeight);
                y += RowHeight + RowGap;
                AddButton(composer, prevBounds, "<", null, delegate
                {
                    MembersMenu(currentPage - 1);
                });
                composer.AddStaticText(L("page", page + 1, pages), textFont, EnumTextOrientation.Center, pageBounds);
                AddButton(composer, nextBounds, ">", null, delegate
                {
                    MembersMenu(currentPage + 1);
                });
            }
            if (renameAll.Count > 0)
            {
                int namesPage = page;
                AddButton(composer, CenteredRow(ref y, width, wideButtonWidth), L("members-resolve-names", renameAll.Count), L("hint-members-resolve-names"), delegate
                {
                    RunEdit(delegate
                    {
                        Reopen(delegate
                        {
                            MembersMenu(namesPage);
                        });
                    }, renameAll.ToArray());
                });
            }
            int gridX = 10 + (width - gridWidth) / 2;
            ElementBounds Cell(int column, int rowY)
            {
                return ElementBounds.Fixed(gridX + column * (cellWidth + GridGap), rowY, cellWidth, RowHeight);
            }
            AddButton(composer, Cell(0, y), L("members-add-player"), L("hint-player-claim"), SubmenuPlayerAccess);
            AddButton(composer, Cell(1, y), L("members-add-group"), L("hint-group-claim"), SubmenuGroupAccess);
            y += RowHeight + RowGap;
            int generalPage = page;
            // Видаляти нікого — «Загальний доступ» один у другому рядку, по центру
            ElementBounds generalBounds = (revokeAll.Count > 0) ? Cell(0, y) : ElementBounds.Fixed(10 + (width - cellWidth) / 2, y, cellWidth, RowHeight);
            AddButton(composer, generalBounds, L("general-claim"), L("hint-general-claim"), delegate
            {
                SubmenuGeneralAccess(generalPage);
            });
            if (revokeAll.Count > 0)
            {
                AddButton(composer, Cell(1, y), removeAllText, L("hint-members-remove-all"), delegate
                {
                    if (!confirmAll)
                    {
                        MembersMenu(page, confirmAll: true);
                        return;
                    }
                    RunEdit(delegate
                    {
                        Reopen(delegate
                        {
                            MembersMenu();
                        });
                    }, revokeAll.ToArray());
                });
            }
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
        }

        // ===================== ДОСТУП ГРАВЦЮ / ГРУПІ / ВСІМ =====================

        // Зміна рівня доступу учасника зі списку: повторна видача за UID просто замінює рівень
        private void MemberAccessMenu(Member member, int returnPage)
        {
            int width = 300;
            GuiComposer composer = Begin("memberaccess", L("title-member-access", member.Name), width, 6);
            int y = 10;
            Action back = delegate
            {
                Reopen(delegate
                {
                    MembersMenu(returnPage);
                });
            };
            AddButton(composer, Row(ref y, width), L("back"), null, back);
            composer.AddStaticText(L("member-current", PermText(member.Flags)), CairoFont.WhiteSmallText(), Row(ref y, width).WithFixedOffset(0.0, 7.0));
            void AddLevel(string textKey, string level)
            {
                AddButton(composer, Row(ref y, width), L(textKey), L("hint-" + textKey), delegate
                {
                    RunEdit(back, string.Format(member.GrantFormat, level));
                });
            }
            AddLevel("player-traverse", "traverse");
            AddLevel("player-use", "use");
            AddLevel("player-all", "all");
            AddButton(composer, Row(ref y, width), L("player-delete"), L("hint-player-delete"), delegate
            {
                RunEdit(back, member.RevokeCommands);
            });
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
        }

        private void AfterAccessChange()
        {
            Reopen(delegate
            {
                MembersMenu();
            });
        }

        private void SubmenuPlayerAccess()
        {
            int width = 300;
            GuiComposer composer = Begin("playermanage", L("title-submenuplayeraccess", ClaimName(selected)), width, 6);
            int y = 10;
            AddButton(composer, Row(ref y, width), L("back"), null, delegate
            {
                MembersMenu();
            });
            AddField(composer, Row(ref y, width), "playerName", L("player-name"));
            void AddAction(string textKey, string commandFormat)
            {
                AddButton(composer, Row(ref y, width), L(textKey), L("hint-" + textKey), delegate
                {
                    string player = Field("playerName");
                    if (!string.IsNullOrWhiteSpace(player))
                    {
                        RunEdit(AfterAccessChange, string.Format(commandFormat, player));
                    }
                });
            }
            AddAction("player-traverse", "/land claim grant {0} traverse");
            AddAction("player-use", "/land claim grant {0} use");
            AddAction("player-all", "/land claim grant {0} all");
            AddAction("player-delete", "/land claim revoke {0}");
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
        }

        private void SubmenuGroupAccess()
        {
            int width = 300;
            GuiComposer composer = Begin("groupmanage", L("title-submenugroupaccess", ClaimName(selected)), width, 6);
            int y = 10;
            AddButton(composer, Row(ref y, width), L("back"), null, delegate
            {
                MembersMenu();
            });
            AddField(composer, Row(ref y, width), "groupName", L("group-name"));
            void AddAction(string textKey, string commandFormat)
            {
                AddButton(composer, Row(ref y, width), L(textKey), L("hint-" + textKey), delegate
                {
                    string group = Field("groupName");
                    if (!string.IsNullOrWhiteSpace(group))
                    {
                        RunEdit(AfterAccessChange, string.Format(commandFormat, group));
                    }
                });
            }
            AddAction("traverse-group", "/land claim grantgroup {0} traverse");
            AddAction("use-group", "/land claim grantgroup {0} use");
            AddAction("all-group", "/land claim grantgroup {0} all");
            AddAction("unassign-group", "/land claim revokegroup {0}");
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
        }

        // Перейменування: завантажити приват і одразу зберегти під новим ім'ям
        private void RenameMenu()
        {
            int width = 300;
            GuiComposer composer = Begin("renameclaim", L("title-rename-claim", ClaimName(selected)), width, 3);
            int y = 10;
            AddButton(composer, Row(ref y, width), L("back"), null, delegate
            {
                ActionMenu();
            });
            AddField(composer, Row(ref y, width), "renameName", L("create-claim-name"));
            AddButton(composer, Row(ref y, width), L("create-claim-save"), L("hint-rename-save"), delegate
            {
                string newName = Field("renameName");
                if (string.IsNullOrWhiteSpace(newName))
                {
                    capi.ShowChatMessage(L("rename-empty"));
                    return;
                }
                if (newName == SaveName(selected))
                {
                    ActionMenu();
                    return;
                }
                int index = ResolveSelected();
                if (index < 0)
                {
                    capi.ShowChatMessage(L("claim-not-found"));
                    BuildListMenu();
                    return;
                }
                List<string> commands = new List<string> { $"/land claim load {index}" };
                AppendSave(commands, selected, newName);
                Run(commands, delegate
                {
                    Reopen(delegate
                    {
                        ActionMenu();
                    });
                }, selected);
            });
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
            // Підставляємо поточну назву, щоб її можна було виправити, а не набирати заново
            base.SingleComposer.GetTextInput("renameName")?.SetValue(SaveName(selected));
        }

        // Загальний доступ — як окремий учасник «Усі гравці»; returnPage — сторінка списку учасників
        private void SubmenuGeneralAccess(int returnPage = 0)
        {
            int width = 300;
            GuiComposer composer = Begin("generalmanage", L("title-submenugeneralaccessu", ClaimName(selected)), width, 6);
            int y = 10;
            AddButton(composer, Row(ref y, width), L("back"), null, delegate
            {
                MembersMenu(returnPage);
            });
            composer.AddStaticText(L("member-current", EveryoneText(selected) ?? L("everyone-off")), CairoFont.WhiteSmallText(), Row(ref y, width).WithFixedOffset(0.0, 7.0));
            AddButton(composer, Row(ref y, width), L("access-all"), L("hint-access-all"), delegate
            {
                RunEdit(AfterAccessChange, "/land claim allowuseeveryone true");
            });
            AddButton(composer, Row(ref y, width), L("access-nothing"), L("hint-access-nothing"), delegate
            {
                RunEdit(AfterAccessChange, "/land claim allowuseeveryone false");
            });
            AddButton(composer, Row(ref y, width), L("access-doors-all"), L("hint-access-doors-all"), delegate
            {
                RunEdit(AfterAccessChange, "/land claim allowtraverseveryone true");
            });
            AddButton(composer, Row(ref y, width), L("access-doors-nothing"), L("hint-access-doors-nothing"), delegate
            {
                RunEdit(AfterAccessChange, "/land claim allowtraverseveryone false");
            });
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
        }

        // ===================== СТВОРЕННЯ І РОЗШИРЕННЯ ПРИВАТУ =====================

        // "Розширити приват": одразу завантажує приват на сервері й відкриває меню розширення
        private void StartExpand()
        {
            int index = ResolveSelected();
            if (index < 0)
            {
                capi.ShowChatMessage(L("claim-not-found"));
                BuildListMenu();
                return;
            }
            if (!Run(new List<string> { $"/land claim load {index}" }))
            {
                return;
            }
            mod.Session = new EditSession
            {
                IsNew = false,
                Name = SaveName(selected),
                Areas = selectedAreas
            };
            EditMenu(isNew: false);
        }

        // Мінімальний розмір області по кожній осі, блоків. Менше мод не відправляє на сервер
        private const int MinClaimSize = 5;

        private static readonly string[] Directions = new string[6] { "north", "south", "west", "east", "up", "down" };

        // Сесія, у якій запам'ятовувати точки. У вікні створення її може не бути (наприклад, після перезапуску
        // гри приват на сервері ще розпочатий, а мод про це вже не знає) — тоді заводимо нову
        private EditSession SessionForPoints(bool isNew)
        {
            if (mod.Session == null && isNew)
            {
                mod.Session = new EditSession
                {
                    IsNew = true
                };
            }
            return mod.Session;
        }

        // Блок під гравцем — те саме, що бере сервер у /land claim start без координат
        private int[] PlayerBlock()
        {
            BlockPos pos = capi.World.Player.Entity.Pos.AsBlockPos;
            return new int[3] { pos.X, pos.Y, pos.Z };
        }

        // Перевірка позначеної області перед add/save, як на сервері: розмір по осі — різниця координат точок.
        // Якщо точки невідомі (ставилися не через мод або вже додані) — не заважаємо, вирішить сервер
        private bool CheckPendingSize(EditSession session)
        {
            if (session?.Start == null || session.End == null)
            {
                return true;
            }
            int sizeX = Math.Abs(session.End[0] - session.Start[0]);
            int sizeY = Math.Abs(session.End[1] - session.Start[1]);
            int sizeZ = Math.Abs(session.End[2] - session.Start[2]);
            if (sizeX >= MinClaimSize && sizeY >= MinClaimSize && sizeZ >= MinClaimSize)
            {
                return true;
            }
            capi.ShowChatMessage(L("claim-too-small", sizeX, sizeY, sizeZ, MinClaimSize));
            return false;
        }

        // Перевірка виділення перед add — те саме, що перевіряє сервер (CmdLand.addCurrentArea і LandClaim.AddArea):
        // розмір; перетин з будь-яким приватом, зокрема з вихідним своїм (на сервері він лишається у списку,
        // поки редагується копія); перетин з уже доданими областями і стикування хоча б з однією
        // областю привату. Не пройшло — пишемо чому й нічого не відправляємо, вікно лишається відкритим.
        // Якщо точки невідомі — не заважаємо, вирішить сервер
        private bool CheckPending(EditSession session, bool isNew)
        {
            if (!CheckPendingSize(session))
            {
                return false;
            }
            if (session?.Start == null || session.End == null)
            {
                return true;
            }
            Cuboidi area = new Cuboidi(session.Start[0], session.Start[1], session.Start[2], session.End[0], session.End[1], session.End[2]);
            List<LandClaim> all = capi.World.Claims?.All;
            if (all != null)
            {
                string me = capi.World.Player.PlayerUID;
                foreach (LandClaim claim in all.ToArray())
                {
                    if (claim != null && claim.Intersects(area))
                    {
                        capi.ShowChatMessage((claim.OwnedByPlayerUid == me) ? L("area-overlaps-own", ClaimName(claim)) : L("area-overlaps", OwnerName(claim)));
                        return false;
                    }
                }
            }
            List<Cuboidi> added = session.Added.Select((int[] a) => new Cuboidi(a)).ToList();
            if (added.Any((Cuboidi a) => a.Intersects(area)))
            {
                capi.ShowChatMessage(L("area-overlaps-added"));
                return false;
            }
            List<Cuboidi> own = new List<Cuboidi>(added);
            if (!isNew && session.Areas != null)
            {
                own.AddRange(session.Areas.Select((int[] a) => new Cuboidi(a)));
            }
            // Стикування як у сервера: торкається гранню, ребром або кутом, але не перетинає
            if (own.Count > 0 && !own.Any((Cuboidi a) => a.IntersectsOrTouches(area)))
            {
                capi.ShowChatMessage(L("area-not-adjacent"));
                return false;
            }
            return true;
        }

        // Ім'я власника привату: у приватів гравців LastKnownOwnerName має вигляд "Player <ім'я>"
        private static string OwnerName(LandClaim claim)
        {
            string name = claim.LastKnownOwnerName ?? "?";
            return name.StartsWith("Player ") ? name.Substring("Player ".Length) : name;
        }

        // Запам'ятати виділення як додану область (після вдалого add)
        private static void RememberAdded(EditSession session, int[] start, int[] end)
        {
            if (session != null && start != null && end != null)
            {
                session.Added.Add(new int[6] { start[0], start[1], start[2], end[0], end[1], end[2] });
            }
        }

        // Те саме розтягування, що робить сервер у /land claim grow (CmdLand.GrowSelection)
        private static void ApplyGrow(int[] start, int[] end, string direction, int size)
        {
            switch (direction)
            {
            case "up":
                if (start[1] < end[1]) end[1] += size; else start[1] += size;
                break;
            case "down":
                if (start[1] < end[1]) start[1] -= size; else end[1] -= size;
                break;
            case "north":
                if (start[2] < end[2]) start[2] -= size; else end[2] -= size;
                break;
            case "south":
                if (start[2] > end[2]) start[2] += size; else end[2] += size;
                break;
            case "east":
                if (start[0] > end[0]) start[0] += size; else end[0] += size;
                break;
            case "west":
                if (start[0] < end[0]) start[0] -= size; else end[0] -= size;
                break;
            }
        }

        // Область, від якої рахуються сторони: та, де стоїть гравець, інакше найближча
        private Cuboidi BaseArea(LandClaim claim)
        {
            Vec3d pos = capi.World.Player.Entity.Pos.XYZ;
            Cuboidi best = null;
            double bestDistance = double.MaxValue;
            foreach (Cuboidi area in claim.Areas)
            {
                if (area.Contains(pos))
                {
                    return area;
                }
                double dx = (double)(area.MinX + area.MaxX) / 2.0 - pos.X;
                double dy = (double)(area.MinY + area.MaxY) / 2.0 - pos.Y;
                double dz = (double)(area.MinZ + area.MaxZ) / 2.0 - pos.Z;
                double distance = dx * dx + dy * dy + dz * dz;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = area;
                }
            }
            return best;
        }

        // «Розширити в сторони»: виділення-смуга потрібної товщини впритул до області привату з одного боку
        // (виділення — один кубоїд, тому сторона одна). Лише виділення: його можна розтягнути, а додає
        // його до привату «Розширити» або «Додати область». Повертає {початок, кінець} або null
        private int[][] DirectionSelection(LandClaim claim, string direction, int n)
        {
            Cuboidi area = BaseArea(claim);
            if (area == null)
            {
                return null;
            }
            int minX = area.MinX;
            int minY = area.MinY;
            int minZ = area.MinZ;
            int maxX = area.MaxX;
            int maxY = area.MaxY;
            int maxZ = area.MaxZ;
            int[][] Box(int x1, int y1, int z1, int x2, int y2, int z2)
            {
                return new int[2][]
                {
                    new int[3] { x1, y1, z1 },
                    new int[3] { x2, y2, z2 }
                };
            }
            switch (direction)
            {
            case "north":
                return Box(minX, minY, minZ - n, maxX, maxY, minZ);
            case "south":
                return Box(minX, minY, maxZ, maxX, maxY, maxZ + n);
            case "west":
                return Box(minX - n, minY, minZ, minX, maxY, maxZ);
            case "east":
                return Box(maxX, minY, minZ, maxX + n, maxY, maxZ);
            case "up":
                n = Math.Min(n, capi.World.BlockAccessor.MapSizeY - maxY);
                return (n > 0) ? Box(minX, maxY, minZ, maxX, maxY + n, maxZ) : null;
            case "down":
                n = Math.Min(n, minY);
                return (n > 0) ? Box(minX, minY - n, minZ, maxX, minY, maxZ) : null;
            default:
                return null;
            }
        }

        private void EditMenu(bool isNew)
        {
            int width = 300;
            int column = 92;
            int columnGap = 12;
            string title = (isNew ? L("title-newclaimmenu") : L("title-expand-claim", ClaimName(selected)));
            GuiComposer composer = Begin("newclaim", title, width, 10);
            int y = 10;
            if (isNew)
            {
                AddButton(composer, Row(ref y, width), L("back"), null, delegate
                {
                    if (mod.Session == null || Run(new List<string> { OptionalPrefix + "/land claim cancel" }))
                    {
                        mod.Session = null;
                        BuildListMenu();
                    }
                });
                AddButton(composer, Row(ref y, width), L("create-claim-new"), L("hint-create-claim-new"), delegate
                {
                    if (Run(new List<string> { "/land claim new" }))
                    {
                        mod.Session = new EditSession
                        {
                            IsNew = true
                        };
                    }
                });
            }
            else
            {
                AddButton(composer, Row(ref y, width), L("cancel-edit"), L("hint-cancel-edit"), delegate
                {
                    if (Run(new List<string> { OptionalPrefix + "/land claim cancel" }))
                    {
                        mod.Session = null;
                        Reopen(delegate
                        {
                            ActionMenu();
                        });
                    }
                });
            }
            // Точки ставимо з явними координатами блока під гравцем (сервер без координат бере те саме),
            // щоб мод знав їх і міг перевірити розмір області до відправки
            AddButton(composer, Row(ref y, width), L("create-claim-start"), L("hint-create-claim-start"), delegate
            {
                EditSession session = SessionForPoints(isNew);
                int[] pos = PlayerBlock();
                Run(new List<string> { $"/land claim start ={pos[0]} ={pos[1]} ={pos[2]}" }, null, null, delegate
                {
                    if (session != null)
                    {
                        session.Start = pos;
                    }
                });
            });
            AddButton(composer, Row(ref y, width), L("create-claim-end"), L("hint-create-claim-end"), delegate
            {
                EditSession session = SessionForPoints(isNew);
                int[] pos = PlayerBlock();
                Run(new List<string> { $"/land claim end ={pos[0]} ={pos[1]} ={pos[2]}" }, null, null, delegate
                {
                    if (session != null)
                    {
                        session.End = pos;
                    }
                });
            });
            // Поля: Північ / Захід / Верх і Південь / Схід / Низ
            string[][] grid = new string[2][]
            {
                new string[3] { "north", "west", "up" },
                new string[3] { "south", "east", "down" }
            };
            foreach (string[] line in grid)
            {
                for (int i = 0; i < line.Length; i++)
                {
                    AddField(composer, ElementBounds.Fixed(10 + (column + columnGap) * i, y, column, RowHeight), line[i], L(line[i]), centered: true);
                }
                y += RowHeight + RowGap;
            }
            if (!isNew)
            {
                AddButton(composer, Row(ref y, width), L("expand-directions"), L("hint-expand-directions"), delegate
                {
                    if (ResolveSelected() < 0)
                    {
                        capi.ShowChatMessage(L("claim-not-found"));
                        return;
                    }
                    List<string> sides = Directions.Where((string d) => NumberField(d) > 0).ToList();
                    if (sides.Count == 0)
                    {
                        capi.ShowChatMessage(L("expand-nothing"));
                        return;
                    }
                    if (sides.Count > 1)
                    {
                        capi.ShowChatMessage(L("expand-one-side"));
                        return;
                    }
                    int amount = NumberField(sides[0]);
                    if (amount < MinClaimSize)
                    {
                        capi.ShowChatMessage(L("expand-too-small", MinClaimSize));
                        return;
                    }
                    int[][] box = DirectionSelection(selected, sides[0], amount);
                    if (box == null)
                    {
                        capi.ShowChatMessage(L("expand-nothing"));
                        return;
                    }
                    // Лише ставимо виділення — точки відомі, далі його можна розтягнути й додати
                    EditSession session = mod.Session;
                    if (session != null)
                    {
                        session.Start = null;
                        session.End = null;
                    }
                    Run(new List<string>
                    {
                        $"/land claim start ={box[0][0]} ={box[0][1]} ={box[0][2]}",
                        $"/land claim end ={box[1][0]} ={box[1][1]} ={box[1][2]}"
                    }, null, null, delegate
                    {
                        if (session != null)
                        {
                            session.Start = box[0];
                            session.End = box[1];
                        }
                    });
                });
            }
            AddButton(composer, Row(ref y, width), L("create-claim-grow"), L("hint-create-claim-grow"), delegate
            {
                List<string> commands = new List<string>();
                List<KeyValuePair<string, int>> grows = new List<KeyValuePair<string, int>>();
                bool allNumbers = true;
                foreach (string direction in Directions)
                {
                    string amount = Field(direction);
                    if (!string.IsNullOrWhiteSpace(amount))
                    {
                        commands.Add("/land claim grow " + direction + " " + amount);
                        if (int.TryParse(amount, out int value))
                        {
                            grows.Add(new KeyValuePair<string, int>(direction, value));
                        }
                        else
                        {
                            allNumbers = false;
                        }
                    }
                }
                if (commands.Count == 0)
                {
                    return;
                }
                // Поки сервер не підтвердив усі розтягування, точки вважаємо невідомими;
                // після успіху перераховуємо їх так само, як це робить сервер
                EditSession session = mod.Session;
                int[] start = (int[])session?.Start?.Clone();
                int[] end = (int[])session?.End?.Clone();
                if (session != null)
                {
                    session.Start = null;
                    session.End = null;
                }
                Run(commands, null, null, delegate
                {
                    if (session == null || start == null || end == null || !allNumbers)
                    {
                        return;
                    }
                    foreach (KeyValuePair<string, int> grow in grows)
                    {
                        ApplyGrow(start, end, grow.Key, grow.Value);
                    }
                    session.Start = start;
                    session.End = end;
                });
            });
            AddButton(composer, Row(ref y, width), L("create-claim-add"), L("hint-create-claim-add"), delegate
            {
                EditSession session = mod.Session;
                if (!CheckPending(session, isNew))
                {
                    return;
                }
                int[] start = session?.Start;
                int[] end = session?.End;
                // Після вдалого add сервер сам стирає точки
                Run(new List<string> { "/land claim add" }, null, null, delegate
                {
                    if (session != null)
                    {
                        RememberAdded(session, start, end);
                        session.Start = null;
                        session.End = null;
                        session.HasArea = true;
                    }
                });
            });
            AddField(composer, Row(ref y, width), "claimName", L("create-claim-name"));
            // У розширенні кнопка називається «Розширити»: додає виділення і зберігає приват
            AddButton(composer, Row(ref y, width), isNew ? L("create-claim-save") : L("expand-save"), isNew ? L("hint-create-claim-save") : L("hint-expand-save"), delegate
            {
                EditSession session = mod.Session;
                string name = Field("claimName");
                if (string.IsNullOrWhiteSpace(name) && !isNew)
                {
                    name = session?.Name ?? SaveName(selected);
                }
                if (!CheckPending(session, isNew))
                {
                    return;
                }
                List<string> commands = new List<string>();
                // Перед збереженням додаємо виділення. Якщо мод знає, що виділення є (точки ставилися
                // через нього), add обов'язковий: його помилка (перетин, не торкається, замале) зупиняє
                // збереження — приват лишається завантаженим з виділенням, вікно відкрите, можна виправити.
                // Якщо точок мод не знає — add необов'язковий: виділення може й не бути, тоді сервер відповість
                // "Start or End not marked", а зберегти все одно треба.
                // Новий приват, у який ще нічого не додано, без цієї області зберігати нічого
                bool pointsKnown = session?.Start != null && session.End != null;
                bool addRequired = pointsKnown || (isNew && session != null && !session.HasArea);
                commands.Add((addRequired ? "" : OptionalPrefix) + "/land claim add");
                AppendSave(commands, isNew ? null : selected, name);
                // Сесію закриваємо, лише якщо сервер справді зберіг приват: інакше можна
                // виправити точки й натиснути «Зберегти» ще раз
                Run(commands, delegate
                {
                    if (isNew)
                    {
                        BuildListMenu();
                    }
                    else
                    {
                        Reopen(delegate
                        {
                            ActionMenu();
                        });
                    }
                }, isNew ? null : selected, delegate
                {
                    mod.Session = null;
                });
            });
            composer.EndChildElements();
            base.SingleComposer = composer.Compose();
            if (!isNew)
            {
                string savedName = mod.Session?.Name ?? SaveName(selected);
                base.SingleComposer.GetTextInput("claimName")?.SetValue(savedName);
                fields["claimName"] = savedName;
                base.SingleComposer.GetDynamicText("ph_claimName")?.SetNewText(string.IsNullOrEmpty(savedName) ? L("create-claim-name") : "");
            }
        }
    }
}
