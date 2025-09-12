using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;


namespace Claim_Navigator
{
    public class Claim_Navigator : ModSystem
    {

        ICoreClientAPI capi;
        ClaimsGui currentGui;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            // Реєстрація гарячої клавіші (CTRL + P) для відкриття GUI
            capi.Input.RegisterHotKey("ClaimNavigatorKey", Lang.Get("claimnavigator:text-in-settings"), GlKeys.P, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
            capi.Input.SetHotKeyHandler("ClaimNavigatorKey", OnClaimNavigator);

        }
        private bool OnClaimNavigator(KeyCombination comb)
        {
            // Якщо GUI вже є і він відкритий → закрити
            if (currentGui != null && currentGui.IsOpened())
            {
                currentGui.TryClose();
                return true;
            }

            capi.SendChatMessage("/land list");
            capi.Event.RegisterCallback(dt =>
            {
                // Створюємо таймер, який через 0.5 секунди відкриє GUI (щоб лог встиг оновитись)         
                List<string> list = LoadClaimsFromLog();
                currentGui = new ClaimsGui(capi, list);
                currentGui.TryOpen();

            }, 500);
            return true;
        }


        private List<string> LoadClaimsFromLog()
        {
            List<string> latestClaimsList = new();
            string path = Path.Combine(capi.GetOrCreateDataPath("Logs"), "client-chat.log");

            // Ключові фрази для визначення початку списку (різні мови)
            string[] searchMarkers = new string[]
            {
                Lang.Get("claimnavigator:search-client-log"),
                "Your land claims:",    // англійська
                "Ваши земельные участки:",   // російська
                "Ваші ділянки:"   // українська
       
            };

            if (!File.Exists(path))
            {
                capi.ShowChatMessage("Файл client-chat.log не знайдено");
                return latestClaimsList;
            }

            string[] lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                var content = reader.ReadToEnd();
                lines = content.Split('\n');
            }

            bool inClaimsList = false;
            List<string> currentList = new();

            foreach (var line in lines)
            {
                // Перевіряємо кожен можливий маркер
                foreach (var marker in searchMarkers)
                {
                    if (!string.IsNullOrEmpty(marker) && line.Contains(marker))
                    {
                        // Зустріли новий блок — скидаємо попередній
                        currentList = new List<string>();
                        inClaimsList = true;
                        goto ContinueOuter; // вихід з вкладеного foreach і перехід на наступний рядок
                    }
                }

                if (inClaimsList)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("("))  // Кінець списку
                    {
                        inClaimsList = false;
                        if (currentList.Count > 0)
                        {
                            latestClaimsList = currentList;
                        }
                        continue;
                    }

                    int colon = line.IndexOf(':');
                    int paren = line.IndexOf('(');

                    if (colon != -1 && paren != -1 && paren > colon)
                    {
                        string name = line.Substring(colon + 1, paren - colon - 1).Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            currentList.Add(name);
                        }
                    }
                }

            ContinueOuter:;
            }

            return latestClaimsList;
        }



    }
    public class ClaimsGui : GuiDialog
    {
        List<string> privatesList;// Список приватів (назви, отримані з логу)
        string selectedClaim;    // Поточний вибраний приват
        int selectedIndex;         // Індекс вибраного привату в списку

        //Конструктор, зберігає список для відображення
        public ClaimsGui(ICoreClientAPI capi, List<string> privatesList) : base(capi)
        {
            this.privatesList = privatesList;
        }

        public override string ToggleKeyCombinationCode => null;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            BuildListMenu(); // початкове меню
        }

        private async Task SendCommandsAsync(params string[] commands)
        {
            foreach (var cmd in commands)
            {
                capi.SendChatMessage(cmd);
                await Task.Delay(1500); // невелика пауза між командами
            }
        }
        // Змінні для роботи скролу списку
        float scrollbarPos = 0f;            // Позиція скролу
        ElementBounds listClipBounds;       // Область, у якій обрізається вміст при прокрутці
        ElementBounds listContentBounds;    // Повна область з усіма елементами списку
        int listTotalHeight;                // Висота всього списку (включно з невидимою частиною)
        int listMaxHeight;                  // Максимальна висота області показу
        const string ScrollbarKey = "privatescrollbar"; // ID скролбара

        private void OnScrollChanged(float val)
        {
            // Оновлюємо позицію вмісту при прокрутці
            scrollbarPos = val;

            int scrollRange = Math.Max(0, listTotalHeight - listMaxHeight);

            // Якщо val > 1 — вважаємо, що це пікселі; інакше — нормалізоване 0..1
            float offset = (val > 1f) ? val : (val * scrollRange);

            if (listContentBounds != null)
            {
                listContentBounds.fixedY = -(int)offset;
                listContentBounds.CalcWorldBounds();
            }
        }

        // ===================== ГОЛОВНЕ МЕНЮ СПИСКУ ПРИВАТІВ =====================
        void BuildListMenu()
        {
            int buttonCount = privatesList.Count; // Кількість кнопок = кількість приватів
            int buttonHeight = 30;                // Висота однієї кнопки
            int verticalSpacing = 5;              // Відстань між кнопками
            int contentWidth = 300;               // Ширина області зі списком
            int visibleRows = 10;                 // Скільки рядків видно без скролу

            listMaxHeight = visibleRows * (buttonHeight + verticalSpacing); // Висота зони показу

            int dialogWidth = contentWidth + 40;  // Загальна ширина вікна
            int dialogHeight = 60 + listMaxHeight;// Загальна висота вікна

            // Межі діалогу (центрування на екрані)
            ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight)
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

            // Зона списку (clip — усе, що виходить за межі, не показується)
            listClipBounds = ElementBounds.Fixed(0, 40, contentWidth, listMaxHeight)
                .WithFixedPadding(10, 10);

            int scrollbarWidth = 12; // Ширина скролбара
            ElementBounds scrollbarBounds = ElementBounds.Fixed(contentWidth - scrollbarWidth, 0, scrollbarWidth, listMaxHeight);

            listTotalHeight = buttonCount * (buttonHeight + verticalSpacing); // Повна висота всіх кнопок

            // Створюємо GUI-композер
            var composer = capi.Gui
                .CreateCompo("privatesdialog", dialogBounds)
                .AddShadedDialogBG(ElementBounds.Fill) // Темний фон
                .AddDialogBG(ElementBounds.Fill, false) // Світлий фон діалогу
                .AddDialogTitleBar(Lang.Get("claimnavigator:title-buildlistmenu"), () => TryClose()) // Заголовок

                .BeginChildElements(listClipBounds)
                    .AddVerticalScrollbar(OnScrollChanged, scrollbarBounds, ScrollbarKey) // Додаємо скрол
                    .BeginClip(ElementBounds.Fixed(0, 0, contentWidth - scrollbarWidth, listMaxHeight))
                        .BeginChildElements(
                            listContentBounds = ElementBounds.Fixed(0, 0, contentWidth - scrollbarWidth, listTotalHeight)
                        );




            ElementBounds current = ElementBounds.Fixed(10, 10, contentWidth - scrollbarWidth - 20, buttonHeight);

            // кнопка створення приватів
            composer.AddSmallButton(Lang.Get("claimnavigator:titile-newclaimmenu"), () =>
            {
                NewClaimMenu();
                return true;
            }, current);

            current = current.BelowCopy(0, verticalSpacing);

            // Додаємо кнопки з назвами приватів
            for (int i = 0; i < buttonCount; i++)
            {
                string name = privatesList[i];
                int index = i;

                composer.AddSmallButton($"{index + 1}. {name}", () =>
                {
                    selectedClaim = name; // Запам'ятовуємо вибраний приват
                    selectedIndex = index;  // Запам'ятовуємо його індекс
                    ActionMenu(name);       // Переходимо до підменю
                    return true;
                }, current);

                current = current.BelowCopy(0, verticalSpacing); // Переносимо позицію для наступної кнопки
            }

            // Завершення формування GUI
            composer.EndChildElements();
            composer.EndClip();
            composer.EndChildElements();
            SingleComposer = composer.Compose();

            // Налаштовуємо скролбар
            var sb = SingleComposer.GetScrollbar(ScrollbarKey);
            if (sb != null)
            {
                sb.SetHeights(listMaxHeight, listTotalHeight);
                sb.CurrentYPosition = 0;
                OnScrollChanged(sb.CurrentYPosition);
            }
        }


        // ===================== ПІДМЕНЮ СТВОРЕННЯ ПРИВАТІВ =====================
        void NewClaimMenu()
        {
            int buttonHeight = 30;
            int verticalSpacing = 10;
            int contentWidth = 300;

            int inputWidth = 80;   // ширина поля
            int inputHeight = 30;  // висота поля
            int gap = 12;          // відстань між колонками

            int dialogWidth = (inputWidth + gap) * 3 + 40;
            int dialogHeight = 500;

            ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight)
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

            ElementBounds contentBounds = ElementBounds.Fixed(0, 40, dialogWidth - 20, dialogHeight - 50)
                .WithFixedPadding(10, 10)
                .WithSizing(ElementSizing.FitToChildren);

            var composer = capi.Gui
                .CreateCompo("newclaim", dialogBounds)
                .AddShadedDialogBG(ElementBounds.Fill)
                .AddDialogBG(ElementBounds.Fill, false)
                .AddDialogTitleBar(Lang.Get("claimnavigator:titile-newclaimmenu"), () => TryClose())
                .BeginChildElements(contentBounds);

            // Кнопка назад (прибрати/змінити якщо не потрібно)
            ElementBounds Bttn = ElementBounds.Fixed(10, 10, contentWidth - 20, buttonHeight);
            composer.AddSmallButton(Lang.Get("claimnavigator:back"), () =>
            {
                BuildListMenu();
                return true;
            }, Bttn);

            Bttn = Bttn.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get(Lang.Get("claimnavigator:create-claim-new")), () =>
            {
                capi.SendChatMessage($"/land claim new");
                return true;
            }, Bttn);

            Bttn = Bttn.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:"), () =>
            {
                capi.SendChatMessage($"/land claim start");
                return true;
            }, Bttn);

            Bttn = Bttn.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:"), () =>
            {
                capi.SendChatMessage($"/land claim end");
                return true;
            }, Bttn);


            // стартова Y-позиція для першого ряду 
            int firstRowY = (int)Bttn.BelowCopy(0, verticalSpacing).fixedY;

            // ======= 1 ряд: Північ, Захід, Верх =======
            ElementBounds northBounds = ElementBounds.Fixed(10, firstRowY, inputWidth, inputHeight);
            ElementBounds westBounds = ElementBounds.Fixed(10 + (inputWidth + gap) * 1, firstRowY, inputWidth, inputHeight);
            ElementBounds upBounds = ElementBounds.Fixed(10 + (inputWidth + gap) * 2, firstRowY, inputWidth, inputHeight);

            // ======= 2 ряд: Південь, Схід, Вниз =======
            int secondRowY = firstRowY + inputHeight + verticalSpacing;
            ElementBounds southBounds = ElementBounds.Fixed(10, secondRowY, inputWidth, inputHeight);
            ElementBounds eastBounds = ElementBounds.Fixed(10 + (inputWidth + gap) * 1, secondRowY, inputWidth, inputHeight);
            ElementBounds downBounds = ElementBounds.Fixed(10 + (inputWidth + gap) * 2, secondRowY, inputWidth, inputHeight);

            // Локальні буфери (за потреби)
            string northBuf = "";
            string southBuf = "";
            string westBuf = "";
            string eastBuf = "";
            string upBuf = "";
            string downBuf = "";
            string nameClaimBuf = "";


            // Колір placeholder (темно/світло-коричневий — налаштуй під свій смак)
            double[] placeholderColor = new double[] { 0.75, 0.62, 0.50, 1.0 }; // RGBA 0..1

            // Вставляємо поля + dynamic placeholder (без 5-го аргументу в AddTextInput!)
            composer.AddTextInput(northBounds, (txt) =>
            {
                northBuf = txt ?? "";
                // приховуємо/показуємо placeholder (SingleComposer вже існує коли користувач вводить)
                SingleComposer?.GetDynamicText("ph_north")?.SetNewText(string.IsNullOrEmpty(northBuf) ? Lang.Get("claimnavigator:north") : "");
            }, CairoFont.TextInput(), "north");
            composer.AddDynamicText(Lang.Get("claimnavigator:north"), CairoFont.WhiteSmallText().WithColor(placeholderColor), northBounds.FlatCopy().WithFixedOffset(5, 5), "ph_north");

            composer.AddTextInput(southBounds, (txt) =>
            {
                southBuf = txt ?? "";
                SingleComposer?.GetDynamicText("ph_south")?.SetNewText(string.IsNullOrEmpty(southBuf) ? Lang.Get("claimnavigator:south") : "");
            }, CairoFont.TextInput(), "south");
            composer.AddDynamicText(Lang.Get("claimnavigator:south"), CairoFont.WhiteSmallText().WithColor(placeholderColor), southBounds.FlatCopy().WithFixedOffset(5, 5), "ph_south");

            composer.AddTextInput(westBounds, (txt) =>
            {
                westBuf = txt ?? "";
                SingleComposer?.GetDynamicText("ph_west")?.SetNewText(string.IsNullOrEmpty(westBuf) ? Lang.Get("claimnavigator:west") : "");
            }, CairoFont.TextInput(), "west");
            composer.AddDynamicText(Lang.Get("claimnavigator:west"), CairoFont.WhiteSmallText().WithColor(placeholderColor), westBounds.FlatCopy().WithFixedOffset(5, 5), "ph_west");

            composer.AddTextInput(eastBounds, (txt) =>
            {
                eastBuf = txt ?? "";
                SingleComposer?.GetDynamicText("ph_east")?.SetNewText(string.IsNullOrEmpty(eastBuf) ? Lang.Get("claimnavigator:east") : "");
            }, CairoFont.TextInput(), "east");
            composer.AddDynamicText(Lang.Get("claimnavigator:east"), CairoFont.WhiteSmallText().WithColor(placeholderColor), eastBounds.FlatCopy().WithFixedOffset(5, 5), "ph_east");

            composer.AddTextInput(upBounds, (txt) =>
            {
                upBuf = txt ?? "";
                SingleComposer?.GetDynamicText("ph_up")?.SetNewText(string.IsNullOrEmpty(upBuf) ? Lang.Get("claimnavigator:up") : "");
            }, CairoFont.TextInput(), "up");
            composer.AddDynamicText(Lang.Get("claimnavigator:up"), CairoFont.WhiteSmallText().WithColor(placeholderColor), upBounds.FlatCopy().WithFixedOffset(5, 5), "ph_up");

            composer.AddTextInput(downBounds, (txt) =>
            {
                downBuf = txt ?? "";
                SingleComposer?.GetDynamicText("ph_down")?.SetNewText(string.IsNullOrEmpty(downBuf) ? Lang.Get("claimnavigator:down") : "");
            }, CairoFont.TextInput(), "down");
            composer.AddDynamicText(Lang.Get("claimnavigator:down"), CairoFont.WhiteSmallText().WithColor(placeholderColor), downBounds.FlatCopy().WithFixedOffset(5, 5), "ph_down");

            // Кнопка "Підтвердити" наприклад
            ElementBounds applyBtn = ElementBounds.Fixed(10, secondRowY + inputHeight + verticalSpacing, contentWidth - 20, buttonHeight);
            composer.AddSmallButton(Lang.Get("claimnavigator:create-claim-grow"), () =>
            {
                // читаємо остаточно (фейлбек на GetTextInput)
                string n = string.IsNullOrWhiteSpace(northBuf) ? SingleComposer?.GetTextInput("north")?.GetText() ?? "" : northBuf;
                string s = string.IsNullOrWhiteSpace(southBuf) ? SingleComposer?.GetTextInput("south")?.GetText() ?? "" : southBuf;
                string w = string.IsNullOrWhiteSpace(westBuf) ? SingleComposer?.GetTextInput("west")?.GetText() ?? "" : westBuf;
                string e = string.IsNullOrWhiteSpace(eastBuf) ? SingleComposer?.GetTextInput("east")?.GetText() ?? "" : eastBuf;
                string up = string.IsNullOrWhiteSpace(upBuf) ? SingleComposer?.GetTextInput("up")?.GetText() ?? "" : upBuf;
                string dn = string.IsNullOrWhiteSpace(downBuf) ? SingleComposer?.GetTextInput("down")?.GetText() ?? "" : downBuf;

                _ = SendCommandsAsync(
                    $"/land claim grow north {n}",
                    $"/land claim grow south {s}",
                    $"/land claim grow west {w}",
                    $"/land claim grow east {e}",
                    $"/land claim grow up {up}",
                    $"/land claim grow down {dn}"
                );

                return true;
            }, applyBtn);

            applyBtn = applyBtn.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:create-claim-add"), () =>
            {
                capi.SendChatMessage($"/land claim add");
                return true;
            }, applyBtn);



            applyBtn = applyBtn.BelowCopy(0, verticalSpacing);
            composer.AddTextInput(applyBtn, (txt) =>
            {
                nameClaimBuf = txt ?? "";
                SingleComposer?.GetDynamicText("ph_name")?.SetNewText(string.IsNullOrEmpty(nameClaimBuf) ? "Ім'я привату" : "");
            }, CairoFont.TextInput(), "claimName");

            composer.AddDynamicText(Lang.Get("claimnavigator:create-claim-name"),
                CairoFont.WhiteSmallText().WithColor(placeholderColor),
                applyBtn.FlatCopy().WithFixedOffset(5, 5),
                "ph_name");

            // Save під полем, тієї ж ширини
            applyBtn = applyBtn.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:create-claim-save"), () =>
            {
                string finalName = string.IsNullOrWhiteSpace(nameClaimBuf)
                    ? SingleComposer?.GetTextInput("claimName")?.GetText() ?? ""
                    : nameClaimBuf;

                capi.SendChatMessage($"/land claim save {finalName}");
                TryClose();
                return true;
            }, applyBtn);

            composer.EndChildElements();
            SingleComposer = composer.Compose();

            // Початкова видимість placeholder-ів (якщо в полях вже є текст)
            SingleComposer?.GetDynamicText("ph_north")?.SetNewText(string.IsNullOrEmpty(SingleComposer?.GetTextInput("north")?.GetText() ?? "") ? Lang.Get("claimnavigator:north") : "");
            SingleComposer?.GetDynamicText("ph_south")?.SetNewText(string.IsNullOrEmpty(SingleComposer?.GetTextInput("south")?.GetText() ?? "") ? Lang.Get("claimnavigator:south") : "");
            SingleComposer?.GetDynamicText("ph_west")?.SetNewText(string.IsNullOrEmpty(SingleComposer?.GetTextInput("west")?.GetText() ?? "") ? Lang.Get("claimnavigator:west") : "");
            SingleComposer?.GetDynamicText("ph_east")?.SetNewText(string.IsNullOrEmpty(SingleComposer?.GetTextInput("east")?.GetText() ?? "") ? Lang.Get("claimnavigator:east") : "");
            SingleComposer?.GetDynamicText("ph_up")?.SetNewText(string.IsNullOrEmpty(SingleComposer?.GetTextInput("up")?.GetText() ?? "") ? Lang.Get("claimnavigator:up") : "");
            SingleComposer?.GetDynamicText("ph_down")?.SetNewText(string.IsNullOrEmpty(SingleComposer?.GetTextInput("down")?.GetText() ?? "") ? Lang.Get("claimnavigator:down") : "");
        }


        // ===================== ПІДМЕНЮ ДІЙ З ПРИВАТОМ =====================
        void ActionMenu(string name)
        {
            int buttonHeight = 30;  // Висота однієї кнопки
            int verticalSpacing = 10;  // Відстань між кнопками
            int contentWidth = 300; // Ширина області зі списком
            int buttonCount = 7;

            int dialogWidth = contentWidth + 40;
            int dialogHeight = 60 + (buttonCount * (buttonHeight + verticalSpacing));

            // Основне вікно
            ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight)
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

            // Вміст
            ElementBounds contentBounds = ElementBounds.Fixed(0, 40, contentWidth, dialogHeight - 50)
                .WithFixedPadding(10, 10)
                .WithSizing(ElementSizing.FitToChildren);

            var composer = capi.Gui
                .CreateCompo("privatedetails", dialogBounds)
                .AddShadedDialogBG(ElementBounds.Fill)
                .AddDialogBG(ElementBounds.Fill, false)
                .AddDialogTitleBar(Lang.Get("claimnavigator:title-actionmenu", name), () => TryClose())
                .BeginChildElements(contentBounds);

            // Кнопки
            ElementBounds current = ElementBounds.Fixed(10, 10, contentWidth - 20, buttonHeight);

            composer.AddSmallButton(Lang.Get("claimnavigator:back"), () =>
            {
                BuildListMenu();
                return true;
            }, current);


            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:аllocate-claim"), () =>
            {
                capi.SendChatMessage($"/land claim load {selectedIndex}");

                TryClose();
                return true;
            }, current);

            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:delete-claim"), () =>
            {
                _ = SendCommandsAsync(
                  $"/land free {selectedIndex}",
                  $"/land free {selectedIndex} confirm",
                  $"/land claim save {name}"
                );
                TryClose();
                return true;
            }, current);

            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:general-claim"), () =>
            {
                SubmenuGeneralAccessu(name);
                return true;
            }, current);

            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:player-claim"), () =>
            {
                SubmenuPlayerAccess(name);
                return true;
            }, current);

            composer.EndChildElements();
            SingleComposer = composer.Compose();
        }





        // ===================== ПІДМЕНЮ ДОБАВИТИ ГРАВЦЯ В ПРИВАТ =====================
        string playerNameBuffer = "";
        void SubmenuPlayerAccess(string name)
        {
            int buttonHeight = 30;  // Висота однієї кнопки
            int verticalSpacing = 10;
            int contentWidth = 300;
            int buttonNum = 5;

            int dialogWidth = contentWidth + 40;
            int dialogHeight = 60 + (buttonNum * (buttonHeight + verticalSpacing)); // назад + інпут + 3 кнопки

            ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight)
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

            ElementBounds contentBounds = ElementBounds.Fixed(0, 40, contentWidth, dialogHeight - 50)
                .WithFixedPadding(10, 10)
                .WithSizing(ElementSizing.FitToChildren);


            var composer = capi.Gui
                .CreateCompo("playermanage", dialogBounds)
                .AddShadedDialogBG(ElementBounds.Fill)
                .AddDialogBG(ElementBounds.Fill, false)
                .AddDialogTitleBar(Lang.Get("claimnavigator:title-submenuplayeraccess", name), () => TryClose())
                .BeginChildElements(contentBounds);

            // Кнопка назад
            ElementBounds current = ElementBounds.Fixed(10, 10, contentWidth - 20, buttonHeight);
            composer.AddSmallButton(Lang.Get("claimnavigator:back"), () =>
            {
                ActionMenu(name);
                return true;
            }, current);

            // Поле для вводу (оновлюємо буфер при кожній зміні)
            current = current.BelowCopy(0, verticalSpacing);
            composer.AddTextInput(
                current,
                (txt) => { playerNameBuffer = txt; },   // <-- важливо: зберігаємо введене
                CairoFont.TextInput(),
                "playerName"
            );

            // Допоміжна функція для безпечного читання імені
            string ReadPlayerName()
            {
                string val = playerNameBuffer;
                if (string.IsNullOrWhiteSpace(val))
                {
                    // підстраховка: читаємо безпосередньо з елемента
                    val = SingleComposer?.GetTextInput("playerName")?.GetText() ?? "";
                }
                return val.Trim();
            }

            // Кнопка "use"
            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:player-use"), () =>
            {
                string playerName = ReadPlayerName();
                if (!string.IsNullOrWhiteSpace(playerName))
                {

                    _ = SendCommandsAsync(
                        $"/land claim load {selectedIndex}",
                        $"/land claim grant {playerName} use",
                        $"/land claim save {name}"
                    );
                }
                TryClose();
                return true;
            }, current);

            // Кнопка "all"
            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:player-all"), () =>
            {
                string playerName = ReadPlayerName();
                if (!string.IsNullOrWhiteSpace(playerName))
                {
                    _ = SendCommandsAsync(
                        $"/land claim load {selectedIndex}",
                        $"/land claim grant {playerName} all",
                        $"/land claim save {name}"
                    );
                }
                TryClose();
                return true;
            }, current);

            // Кнопка "revoke"
            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:player-delete"), () =>
            {
                string playerName = ReadPlayerName();
                if (!string.IsNullOrWhiteSpace(playerName))
                {
                    _ = SendCommandsAsync(
                        $"/land claim load {selectedIndex}",
                        $"/land claim revoke {playerName}",
                        $"/land claim save {name}"
                    );
                }
                TryClose();
                return true;
            }, current);

            composer.EndChildElements();
            SingleComposer = composer.Compose();
        }


        // ===================== ПІДМЕНЮ ДІЙ ЗАГАЛЬНИЙ ДОСТУП ДО ПРИВАТУ =====================
        void SubmenuGeneralAccessu(string name)
        {
            int buttonHeight = 30;
            int verticalSpacing = 10;
            int contentWidth = 300;
            int buttonNum = 4;

            int dialogWidth = contentWidth + 40;
            int dialogHeight = 60 + (buttonNum * (buttonHeight + verticalSpacing)); // назад + 2 кнопки

            ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight)
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

            ElementBounds contentBounds = ElementBounds.Fixed(0, 40, contentWidth, dialogHeight - 50)
                .WithFixedPadding(10, 10)
                .WithSizing(ElementSizing.FitToChildren);

            var composer = capi.Gui
                .CreateCompo("playermanage", dialogBounds)
                .AddShadedDialogBG(ElementBounds.Fill)
                .AddDialogBG(ElementBounds.Fill, false)
                .AddDialogTitleBar(Lang.Get("claimnavigator:title-submenugeneralaccessu", name), () => TryClose())
                .BeginChildElements(contentBounds);

            // Кнопка назад
            ElementBounds current = ElementBounds.Fixed(10, 10, contentWidth - 20, buttonHeight);
            composer.AddSmallButton(Lang.Get("claimnavigator:back"), () =>
            {
                ActionMenu(name);
                return true;
            }, current);

            // Кнопка добавити
            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:access-all"), () =>
            {
                _ = SendCommandsAsync(
                        $"/land claim load {selectedIndex}",
                        $"/land claim allowuseeveryone true",
                        $"/land claim save {name}"
                );
                TryClose();
                return true;
            }, current);

            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton(Lang.Get("claimnavigator:access-nothing"), () =>
            {
                _ = SendCommandsAsync(
                        $"/land claim load {selectedIndex}",
                        $"/land claim allowuseeveryone false",
                        $"/land claim save {name}"
                );
                TryClose();
                return true;
            }, current);

            composer.EndChildElements();
            SingleComposer = composer.Compose();
        }

    }
}


