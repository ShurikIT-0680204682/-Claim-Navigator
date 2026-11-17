using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Claim_Navigator
{
    // Незавершене редагування привату на сервері (/land claim new або /land claim load).
    // Зберігається в моді, а не у вікні: вікно можна закрити, дійти до потрібної точки і відкрити знову
    public class EditSession
    {
        public bool IsNew;

        public string Name;

        // Області привату на момент завантаження, за ними приват знаходиться заново
        public List<int[]> Areas;

        // Позначені, але ще не додані точки області (абсолютні координати блока) або null,
        // якщо точку не позначено або мод не знає її напевно. Потрібні для перевірки мінімального розміру
        public int[] Start;

        public int[] End;

        // У приват уже додано хоча б одну область (сервер відповів на add успіхом)
        public bool HasArea;
    }

    public class Claim_Navigator : ModSystem
    {
        private const string NamesFile = "claimnavigator-names.json";

        private ICoreClientAPI capi;

        private ClaimsGui currentGui;

        private Dictionary<string, string> names = new Dictionary<string, string>();

        public EditSession Session;

        // Йде відправка черги команд — нові дії поки не приймаються
        public bool Busy;

        // Коли мод відправив останню команду (capi.ElapsedMilliseconds), 0 — ще не відправляв
        public long LastCommandMs;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            // Завантажуємо збережені ніки гравців (UID → нік)
            try
            {
                names = capi.LoadModConfig<Dictionary<string, string>>(NamesFile) ?? new Dictionary<string, string>();
            }
            catch (Exception)
            {
                names = new Dictionary<string, string>();
            }
            // Реєстрація гарячої клавіші (CTRL + P) для відкриття GUI
            capi.Input.RegisterHotKey("ClaimNavigatorKey", Lang.Get("claimnavigator:text-in-settings"), GlKeys.P, HotkeyType.GUIOrOtherControls, altPressed: false, ctrlPressed: true);
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
            RefreshNames();
            currentGui = new ClaimsGui(capi, this);
            currentGui.TryOpen();
            return true;
        }

        // Сервер втрачає імена допущених гравців при кожному /land claim load, тому запам'ятовуємо
        // у себе все, що встигли побачити: імена з приватів, власників приватів, гравців онлайн
        public void RefreshNames()
        {
            bool changed = false;
            List<LandClaim> all = capi.World.Claims?.All;
            if (all != null)
            {
                foreach (LandClaim claim in all.ToArray())
                {
                    if (claim == null)
                    {
                        continue;
                    }
                    if (claim.PermittedPlayerLastKnownPlayerName != null)
                    {
                        foreach (KeyValuePair<string, string> pair in claim.PermittedPlayerLastKnownPlayerName)
                        {
                            changed |= Remember(pair.Key, pair.Value);
                        }
                    }
                    // LastKnownOwnerName у приватів гравців має вигляд "Player <ім'я>"
                    if (claim.OwnedByPlayerUid != null && claim.LastKnownOwnerName != null && claim.LastKnownOwnerName.StartsWith("Player "))
                    {
                        changed |= Remember(claim.OwnedByPlayerUid, claim.LastKnownOwnerName.Substring("Player ".Length));
                    }
                }
            }
            foreach (IPlayer player in capi.World.AllPlayers)
            {
                if (player != null)
                {
                    changed |= Remember(player.PlayerUID, player.PlayerName);
                }
            }
            if (changed)
            {
                try
                {
                    capi.StoreModConfig(names, NamesFile);
                }
                catch (Exception)
                {
                }
            }
        }

        private bool Remember(string uid, string name)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(name) || name == uid)
            {
                return false;
            }
            if (names.TryGetValue(uid, out string known) && known == name)
            {
                return false;
            }
            names[uid] = name;
            return true;
        }

        // Ім'я гравця за UID або null, якщо воно ніде не траплялося
        public string NameFor(string uid, LandClaim claim)
        {
            if (claim?.PermittedPlayerLastKnownPlayerName != null && claim.PermittedPlayerLastKnownPlayerName.TryGetValue(uid, out string name) && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
            if (names.TryGetValue(uid, out name))
            {
                return name;
            }
            return capi.World.PlayerByUid(uid)?.PlayerName;
        }
    }
}
