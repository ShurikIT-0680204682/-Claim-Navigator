using System;
using Cairo;
using Vintagestory.API.Client;

namespace Claim_Navigator
{
    // Звичайний вертикальний скролбар, який можна сховати, коли вміст вміщується:
    // тоді він не малюється (ні доріжка, ні повзунок) і не реагує на мишу та клавіші.
    // Доріжка малюється при компонуванні, тому після зміни Hidden потрібен ReCompose
    public class HideableScrollbar : GuiElementScrollbar
    {
        public bool Hidden;

        public HideableScrollbar(ICoreClientAPI capi, Action<float> onNewScrollbarValue, ElementBounds bounds)
            : base(capi, onNewScrollbarValue, bounds)
        {
        }

        public override void ComposeElements(Context ctxStatic, ImageSurface surface)
        {
            // Схований: лише рахуємо межі, доріжку й повзунок не малюємо
            if (Hidden)
            {
                Bounds.CalcWorldBounds();
                return;
            }
            base.ComposeElements(ctxStatic, surface);
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            if (!Hidden)
            {
                base.RenderInteractiveElements(deltaTime);
            }
        }

        public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
        {
            if (!Hidden)
            {
                base.OnMouseWheel(api, args);
            }
        }

        public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
        {
            if (!Hidden)
            {
                base.OnMouseDownOnElement(api, args);
            }
        }

        public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
        {
            if (!Hidden)
            {
                base.OnMouseMove(api, args);
            }
        }

        public override void OnKeyDown(ICoreClientAPI api, KeyEvent args)
        {
            if (!Hidden)
            {
                base.OnKeyDown(api, args);
            }
        }
    }
}
