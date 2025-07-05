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

        private List<string> LoadPrivatesFromLog()
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
                if (line.Contains("Ваши земельные участки:"))
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
    }
    public class PrivatesGui : GuiDialog
    {
        List<string> privatesList;
        public PrivatesGui(ICoreClientAPI capi, List<string> privatesList) : base(capi)
        {
            this.privatesList = privatesList;
        }

        public override string ToggleKeyCombinationCode => null;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedPadding(10, 10);

            var composer = capi.Gui
                .CreateCompo("privatesdialog", dialogBounds)
                .AddDialogTitleBar("Привати", () => TryClose());

            ElementBounds current = ElementBounds.Fixed(10, 40, 280, 25);

            for (int i = 0; i < privatesList.Count; i++)
            {
                string name = privatesList[i];
                string label = $"{i + 1}. {name}";

                composer.AddSmallButton(label, () =>
                {
                    capi.ShowChatMessage($"Вибрано приват: {name}");
                    return true;
                }, current);

                current = current.BelowCopy(0, 4); // Відступ вниз
            }

            SingleComposer = composer.Compose();
        }



    }
}
