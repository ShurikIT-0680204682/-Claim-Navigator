using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;


namespace Private_Navigator
{
    public class Private_Navigator : ModSystem
    {

        ICoreClientAPI capi;


        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            capi.ChatCommands.Create("privates")
                .HandleWith(privates);
        }

        private TextCommandResult privates(TextCommandCallingArgs args)
        {
            List<string> list = LoadPrivatesFromLog();
            PrivatesGui gui = new PrivatesGui(capi, list);
            gui.TryOpen();
            return TextCommandResult.Success("Вікно відкрито.");
        }

        /* private List<string> LoadPrivatesFromLog()
         {
             List<string> privatesList = new();

             string path = Path.Combine(capi.GetOrCreateDataPath("Logs"), "client-chat.log");
             if (!File.Exists(path))
             {
                 capi.ShowChatMessage("Файл client-chat.log не знайдено");
                 return privatesList;
             }

             string[] lines;
             using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
             using (var reader = new StreamReader(fs))
             {
                 var content = reader.ReadToEnd();
                 lines = content.Split('\n');
             }

             bool inPrivatesList = false;

             foreach (var line in lines)
             {
                 if (line.Contains("участки:"))
                 {
                     inPrivatesList = true;
                     continue;
                 }

                 if (inPrivatesList)
                 {
                     if (!line.Contains("@ 0")) break; // Кінець списку

                     int colon = line.IndexOf(':');
                     int paren = line.IndexOf('(');

                     if (colon != -1 && paren != -1 && paren > colon)
                     {
                         string name = line.Substring(colon + 1, paren - colon - 1).Trim();
                         if (!string.IsNullOrEmpty(name))
                         {
                             privatesList.Add(name);
                         }
                     }
                 }
             }

             return privatesList;
         }
    }*/
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

        void BuildListMenu()
        {
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedPadding(10, 10);

            var composer = capi.Gui
                .CreateCompo("privatesdialog", dialogBounds)
                .AddDialogTitleBar("Список приватів", () => TryClose());

            ElementBounds current = ElementBounds.Fixed(10, 40, 300, 30);

            for (int i = 0; i < privatesList.Count; i++)
            {
                string name = privatesList[i];
                int index = i; // ← Зберігаємо поточний індекс, щоб використати в замиканні

                composer.AddSmallButton($"{index + 1}. {name}", () =>
                {
                    selectedPrivate = name;
                    selectedIndex = index;
                    BuildActionMenu(name);
                    return true;
                }, current);

                current = current.BelowCopy(0, 5);
            }


            SingleComposer = composer.Compose();
        }

        void BuildActionMenu(string name)
        {
            // Знову створюємо новий GUI в тому ж вікні
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedPadding(10, 10);

            var composer = capi.Gui
                .CreateCompo("privatedetails", dialogBounds)
                .AddDialogTitleBar($"Дії: {name}", () => TryClose());

            ElementBounds btn = ElementBounds.Fixed(10, 40, 200, 30);

            composer.AddSmallButton("← Назад", () =>
            {
                BuildListMenu();
                return true;
            }, btn);

            composer.AddSmallButton("Виділити", () =>
            {
                capi.SendChatMessage($"/land claim load {selectedIndex}");
                TryClose();
                return true;
            }, btn.BelowCopy(0, 10));

            composer.AddSmallButton("Видалити", () =>
            {
                capi.SendChatMessage($"/land remove {selectedIndex}");
                TryClose();
                return true;
            }, btn.BelowCopy(0, 50));

            composer.AddSmallButton("Телепорт", () =>
            {
                capi.SendChatMessage($"/land tp {selectedIndex}");
                TryClose();
                return true;
            }, btn.BelowCopy(0, 90));

            SingleComposer = composer.Compose();
        }
    }

}
