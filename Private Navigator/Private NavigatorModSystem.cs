using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;


namespace Private_Navigator
{
    public class Private_Navigator : ModSystem
    {

        ICoreClientAPI capi;
        PrivatesGui currentGui;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            capi.Input.RegisterHotKey("PrivateNavigatorKey", "відкрити Private Navigator", GlKeys.P, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
            capi.Input.SetHotKeyHandler("PrivateNavigatorKey", OnPrivateNavigator);

        }
        private bool OnPrivateNavigator(KeyCombination comb)
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
                List<string> list = LoadPrivatesFromLog();
                currentGui = new PrivatesGui(capi, list);
                currentGui.TryOpen();

            }, 500);
            return true;
        }


        private List<string> LoadPrivatesFromLog()
        {
            List<string> latestPrivatesList = new();
            string path = Path.Combine(capi.GetOrCreateDataPath("Logs"), "client-chat.log");
            if (!File.Exists(path))
            {
                capi.ShowChatMessage("Файл client-chat.log не знайдено");
                return latestPrivatesList;
            }

            string[] lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                var content = reader.ReadToEnd();
                lines = content.Split('\n');
            }

            bool inPrivatesList = false;
            List<string> currentList = new();

            foreach (var line in lines)
            {
                if (line.Contains("Ваши земельные участки:"))
                {
                    // Зустріли новий блок — скидаємо попередній
                    currentList = new List<string>();
                    inPrivatesList = true;
                    continue;
                }

                if (inPrivatesList)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("("))  // Кінець списку
                    {
                        inPrivatesList = false;
                        if (currentList.Count > 0)
                        {
                            latestPrivatesList = currentList;
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
            }

            return latestPrivatesList;
        }


    }
    public class PrivatesGui : GuiDialog
    {
        List<string> privatesList;
        string selectedPrivate;
        int selectedIndex;
        public PrivatesGui(ICoreClientAPI capi, List<string> privatesList) : base(capi)
        {
            this.privatesList = privatesList;
        }

        public override string ToggleKeyCombinationCode => null;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            BuildListMenu(); // початкове меню
        }


        float scrollbarPos = 0f;
        ElementBounds listClipBounds;
        ElementBounds listContentBounds;
        int listTotalHeight;
        int listMaxHeight;
        const string ScrollbarKey = "privatescrollbar";

        private void OnScrollChanged(float val)
        {
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


        void BuildListMenu()
        {
            int buttonCount = privatesList.Count;
            int buttonHeight = 30;
            int verticalSpacing = 5;
            int contentWidth = 300;

            int visibleRows = 3;
            listMaxHeight = visibleRows * (buttonHeight + verticalSpacing);

            int dialogWidth = contentWidth + 40;
            int dialogHeight = 60 + listMaxHeight;

            ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, dialogWidth, dialogHeight)
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

            listClipBounds = ElementBounds.Fixed(0, 40, contentWidth, listMaxHeight)
                .WithFixedPadding(10, 10);

            int scrollbarWidth = 12;
            ElementBounds scrollbarBounds = ElementBounds.Fixed(contentWidth - scrollbarWidth, 0, scrollbarWidth, listMaxHeight);

            listTotalHeight = buttonCount * (buttonHeight + verticalSpacing);

            var composer = capi.Gui
                .CreateCompo("privatesdialog", dialogBounds)
                .AddShadedDialogBG(ElementBounds.Fill)
                .AddDialogBG(ElementBounds.Fill, false)
                .AddDialogTitleBar("Список приватів", () => TryClose())

                .BeginChildElements(listClipBounds)
                    .AddVerticalScrollbar(OnScrollChanged, scrollbarBounds, ScrollbarKey)
                    .BeginClip(ElementBounds.Fixed(0, 0, contentWidth - scrollbarWidth, listMaxHeight))
                        .BeginChildElements(
                            listContentBounds = ElementBounds.Fixed(0, 0, contentWidth - scrollbarWidth, listTotalHeight)
                        );

            ElementBounds current = ElementBounds.Fixed(10, 10, contentWidth - scrollbarWidth - 20, buttonHeight);

            for (int i = 0; i < buttonCount; i++)
            {
                string name = privatesList[i];
                int index = i;

                composer.AddSmallButton($"{index + 1}. {name}", () =>
                {
                    selectedPrivate = name;
                    selectedIndex = index;
                    BuildActionMenu(name);
                    return true;
                }, current);

                current = current.BelowCopy(0, verticalSpacing);
            }

            composer.EndChildElements(); // закриваємо внутрішній контейнер
            composer.EndClip();          // вихід з clip-зони
            composer.EndChildElements(); // область зі скролом

            SingleComposer = composer.Compose();

            var sb = SingleComposer.GetScrollbar(ScrollbarKey);
            if (sb != null)
            {
                sb.SetHeights(listMaxHeight, listTotalHeight);
                sb.CurrentYPosition = 0;

                OnScrollChanged(sb.CurrentYPosition);
            }

        }





        void BuildActionMenu(string name)
        {
            int buttonHeight = 30;
            int verticalSpacing = 10;
            int contentWidth = 300;
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
                .AddDialogTitleBar($"Дії: {name}", () => TryClose())
                .BeginChildElements(contentBounds);

            // Кнопки
            ElementBounds current = ElementBounds.Fixed(10, 10, contentWidth - 20, buttonHeight);

            composer.AddSmallButton("← Назад", () =>
            {
                BuildListMenu();
                return true;
            }, current);


            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton("Виділити", () =>
            {
                capi.SendChatMessage($"/land claim load {selectedIndex}");

                TryClose();
                return true;
            }, current);

            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton("Видалити", () =>
            {
                capi.SendChatMessage($"/land free {selectedIndex}");
                capi.SendChatMessage($"/land free {selectedIndex} confirm");
                capi.SendChatMessage($"/land list");
                TryClose();
                return true;
            }, current);

            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton("All доступ", () =>
            {
                capi.SendChatMessage($"/land claim load {selectedIndex}");
                capi.SendChatMessage($"/land claim allowuseeveryone true");
                capi.SendChatMessage($"/land claim save {name}");
                TryClose();
                return true;
            }, current);


            current = current.BelowCopy(0, verticalSpacing);
            composer.AddSmallButton("Nothing доступ", () =>
            {
                capi.SendChatMessage($"/land claim load {selectedIndex}");
                capi.SendChatMessage($"/land claim allowuseeveryone false");
                capi.SendChatMessage($"/land claim save {name}");
                TryClose();
                return true;
            }, current);



            /* current = current.BelowCopy(0, verticalSpacing);
             composer.AddSmallButton("Добавление игрока", () =>
             {
                 capi.SendChatMessage($"/land claim load {selectedIndex}");
                 capi.SendChatMessage($"/land claim load {selectedIndex}");
                 TryClose();
                 return true;
             }, current);*/

            composer.EndChildElements();
            SingleComposer = composer.Compose();
        }
    }
}

