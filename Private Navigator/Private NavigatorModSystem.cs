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
            PrivatesGui gui = new PrivatesGui(capi);
            gui.TryOpen();
            return TextCommandResult.Success("Вікно відкрито.");
        }


    }
    public class PrivatesGui : GuiDialog
    {
        public PrivatesGui(ICoreClientAPI capi) : base(capi)
        {

        }

        public override string ToggleKeyCombinationCode => null;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            // Позиція діалогу по центру
            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedPadding(10, 10);

            // Область для тексту
            ElementBounds textBounds = ElementBounds.Fixed(0, 40, 300, 30);

            // Створення GUI
            SingleComposer = capi.Gui
                .CreateCompo("privatesdialog", dialogBounds)
                .AddDialogTitleBar("Привати", () => TryClose())
                .AddStaticText("Тут буде список приватів", CairoFont.WhiteSmallText(), textBounds)
                .Compose();
        }
    }
}
