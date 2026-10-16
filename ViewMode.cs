// ---------------------------------------------------------------------------
//  First or third person: what a DayZ server allows, read from its tags.
//
//  DayZ 1.30 REVERSED THE "no3rd" TAG
//    Until 1.30 a server tagged "no3rd" had third person turned off. 1.30
//    replaced the on/off setting with three modes - ThirdPersonMode DISABLED,
//    ENABLED, VEHICLES_ONLY (0, 1, 2) - and the tag is now set whenever the
//    mode is not DISABLED. So "no3rd" now means third person IS allowed.
//
//    Measured 16 October 2026, live, against 1.30.164078 servers: all 40
//    servers named "3PP" carried no3rd, and 28 of 38 named "1PP" did not.
//    The game's own 1.30 browser reads it the same way
//    (serverbrowserdetailscontainer.c: a value of 1 is
//    ThirdPersonMode.ENABLED, "third person").
//
//    Servers still on 1.29 keep the old meaning and read backwards here -
//    but a 1.30 game cannot join them, so they are not worth a version check
//    that the server list has no version to make.
//
//  VEHICLES ONLY IS NOT PUBLISHED
//    The tag is a single yes/no, and neither the tags nor the A2S rules carry
//    anything more, so "third person in vehicles only" looks exactly like
//    full third person. The one hint left is the name: a server that calls
//    itself 1PP but allows third person is almost always vehicles only, and
//    is shown as V3PP. It is a guess, and the only one there is.
// ---------------------------------------------------------------------------

using System;
using System.Text.RegularExpressions;

namespace ABeautifulPotatoLauncher
{
    internal enum ViewMode
    {
        Unknown,
        FirstPerson,
        ThirdPerson,
        VehiclesOnly
    }

    internal static class ViewModes
    {
        private static readonly Regex Named1pp =
            new Regex(@"\b1\s?pp\b|first[\s-]?person", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Named3pp =
            new Regex(@"\b3\s?pp\b|third[\s-]?person|3rd[\s-]?person", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>What a DayZ server allows, from its tags and - for vehicles only - its name.</summary>
        public static ViewMode Of(string tags, string name)
        {
            if (Games.IsZomboid || string.IsNullOrEmpty(tags)) return ViewMode.Unknown;

            bool thirdAllowed = false;
            foreach (var part in tags.Split(','))
                if (part.Trim().Equals("no3rd", StringComparison.OrdinalIgnoreCase)) { thirdAllowed = true; break; }

            if (!thirdAllowed) return ViewMode.FirstPerson;
            string n = name ?? "";
            return Named1pp.IsMatch(n) && !Named3pp.IsMatch(n) ? ViewMode.VehiclesOnly : ViewMode.ThirdPerson;
        }

        public static string Label(ViewMode mode)
        {
            switch (mode)
            {
                case ViewMode.FirstPerson: return "1PP";
                case ViewMode.ThirdPerson: return "3PP";
                case ViewMode.VehiclesOnly: return "V3PP";
                default: return "";
            }
        }
    }
}
