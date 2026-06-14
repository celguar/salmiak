using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Salmiak.Core.Formats;

public enum SpellFieldKind { Int, Float, Text }

public readonly record struct SpellField(string Name, int Index, SpellFieldKind Kind, string EditValue, string Display);

public sealed class SpellRecord : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPc(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    private uint _id; public uint Id { get => _id; set { _id = value; OnPc(nameof(Id)); } }
    private string _name = ""; public string Name { get => _name; set { _name = value; OnPc(nameof(Name)); } }
    private string _rank = ""; public string Rank { get => _rank; set { _rank = value; OnPc(nameof(Rank)); } }
    private string _iconPath = ""; public string IconPath { get => _iconPath; set { _iconPath = value; OnPc(nameof(IconPath)); } }

    public int RecIndex;

    public string Description = "";
    public string ToolTip = "";

    public uint School, Category, Dispel, Mechanic;
    public uint Attributes, AttributesEx, AttributesEx2, AttributesEx3, AttributesEx4;
    public uint CastingTimeIndex, RecoveryTime, CategoryRecoveryTime, DurationIndex;
    public uint PowerType, ManaCost, ManaCostPerLevel, ManaPerSecond, RangeIndex;
    public float Speed;
    public uint SpellVisual, SpellIconId, ActiveIconId;
    public uint SpellFamilyName;
    public ulong SpellFamilyFlags;
    public uint MaxLevel, BaseLevel, SpellLevel, ProcChance, ProcCharges;
    public uint Targets, Stances, StancesNot;

    public readonly uint[] Effect = new uint[3];
    public readonly int[]  EffectBasePoints = new int[3];
    public readonly int[]  EffectDieSides = new int[3];
    public readonly uint[] EffectApplyAuraName = new uint[3];
    public readonly uint[] EffectImplicitTargetA = new uint[3];
    public readonly int[]  EffectMiscValue = new int[3];
    public readonly uint[] EffectTriggerSpell = new uint[3];
    public readonly uint[] EffectRadiusIndex = new uint[3];

    public int CastTimeMs;
    public int DurationMs;
    public float RangeMin, RangeMax;
}

public sealed class SpellDatabase
{
    private DbcFile _spell = null!;
    public IReadOnlyList<SpellRecord> Spells { get; private set; } = Array.Empty<SpellRecord>();

    private Dictionary<uint, string> _iconPath = new();
    private Dictionary<uint, int> _castTime = new();
    private Dictionary<uint, int> _duration = new();
    private Dictionary<uint, float> _rangeMin = new(), _rangeMax = new(), _radius = new();

    private DbcFile? _spellVisual, _spellVisualKit;
    private Dictionary<uint, string> _effModel = new();

    private const int F_School = 1, F_Category = 2, F_Dispel = 4, F_Mechanic = 5;
    private const int F_Attributes = 6;
    private const int F_Stances = 11, F_StancesNot = 12, F_Targets = 13;
    private const int F_CastingTimeIndex = 18, F_RecoveryTime = 19, F_CategoryRecoveryTime = 20;
    private const int F_ProcChance = 25, F_ProcCharges = 26;
    private const int F_MaxLevel = 27, F_BaseLevel = 28, F_SpellLevel = 29;
    private const int F_DurationIndex = 30, F_PowerType = 31, F_ManaCost = 32, F_ManaCostPerLevel = 33, F_ManaPerSecond = 34;
    private const int F_RangeIndex = 36, F_Speed = 37;
    private const int F_Effect = 61, F_EffectDieSides = 64, F_EffectBasePoints = 76;
    private const int F_EffectImplicitTargetA = 82, F_EffectRadiusIndex = 88, F_EffectApplyAuraName = 91;
    private const int F_EffectMiscValue = 106, F_EffectTriggerSpell = 109;
    private const int F_SpellVisual = 115, F_SpellIconId = 117, F_ActiveIconId = 118;
    private const int F_SpellName = 120, F_Rank = 129, F_Description = 138, F_ToolTip = 147;
    private const int F_SpellFamilyName = 160, F_SpellFamilyFlagsLo = 161, F_SpellFamilyFlagsHi = 162;

    public static SpellDatabase Load(Func<string, Stream> open)
    {
        var db = new SpellDatabase();

        using (var s = open(@"DBFilesClient\Spell.dbc")) db._spell = DbcFile.Read(s);

        var iconPath = LoadStringDbc(open, @"DBFilesClient\SpellIcon.dbc", 1);
        var castTime = LoadIntDbc(open, @"DBFilesClient\SpellCastTimes.dbc", 1);
        var duration = LoadIntDbc(open, @"DBFilesClient\SpellDuration.dbc", 1);
        var (rangeMin, rangeMax) = LoadRangeDbc(open);
        var radius = LoadFloatDbc(open, @"DBFilesClient\SpellRadius.dbc", 1);
        db._iconPath = iconPath; db._castTime = castTime; db._duration = duration;
        db._rangeMin = rangeMin; db._rangeMax = rangeMax; db._radius = radius;

        try { using var s = open(@"DBFilesClient\SpellVisual.dbc"); db._spellVisual = DbcFile.Read(s); } catch { }
        try { using var s = open(@"DBFilesClient\SpellVisualKit.dbc"); db._spellVisualKit = DbcFile.Read(s); } catch { }
        db._effModel = LoadStringDbc(open, @"DBFilesClient\SpellVisualEffectName.dbc", 2);

        var spell = db._spell;
        db._list = new List<SpellRecord>(spell.RecordCount);
        for (int i = 0; i < spell.RecordCount; i++)
            db._list.Add(db.BuildSpellRecord(i));

        db.Spells = db._list;
        return db;
    }

    private readonly List<SpellRecord> _emptyList = new();
    private List<SpellRecord> _list;
    public SpellDatabase() { _list = _emptyList; }

    private SpellRecord BuildSpellRecord(int i)
    {
        var r = new SpellRecord { RecIndex = i };
        PopulateRecord(r, i);
        return r;
    }

    private void PopulateRecord(SpellRecord r, int i)
    {
        var spell = _spell;
        r.RecIndex = i;
        r.Id = spell.GetU(i, 0);

        r.School = spell.GetU(i, F_School);
        r.Category = spell.GetU(i, F_Category);
        r.Dispel = spell.GetU(i, F_Dispel);
        r.Mechanic = spell.GetU(i, F_Mechanic);
        r.Attributes   = spell.GetU(i, F_Attributes);
        r.AttributesEx = spell.GetU(i, F_Attributes + 1);
        r.AttributesEx2= spell.GetU(i, F_Attributes + 2);
        r.AttributesEx3= spell.GetU(i, F_Attributes + 3);
        r.AttributesEx4= spell.GetU(i, F_Attributes + 4);
        r.Stances = spell.GetU(i, F_Stances);
        r.StancesNot = spell.GetU(i, F_StancesNot);
        r.Targets = spell.GetU(i, F_Targets);
        r.CastingTimeIndex = spell.GetU(i, F_CastingTimeIndex);
        r.RecoveryTime = spell.GetU(i, F_RecoveryTime);
        r.CategoryRecoveryTime = spell.GetU(i, F_CategoryRecoveryTime);
        r.ProcChance = spell.GetU(i, F_ProcChance);
        r.ProcCharges = spell.GetU(i, F_ProcCharges);
        r.MaxLevel = spell.GetU(i, F_MaxLevel);
        r.BaseLevel = spell.GetU(i, F_BaseLevel);
        r.SpellLevel = spell.GetU(i, F_SpellLevel);
        r.DurationIndex = spell.GetU(i, F_DurationIndex);
        r.PowerType = spell.GetU(i, F_PowerType);
        r.ManaCost = spell.GetU(i, F_ManaCost);
        r.ManaCostPerLevel = spell.GetU(i, F_ManaCostPerLevel);
        r.ManaPerSecond = spell.GetU(i, F_ManaPerSecond);
        r.RangeIndex = spell.GetU(i, F_RangeIndex);
        r.Speed = spell.GetF(i, F_Speed);
        r.SpellVisual = spell.GetU(i, F_SpellVisual);
        r.SpellIconId = spell.GetU(i, F_SpellIconId);
        r.ActiveIconId = spell.GetU(i, F_ActiveIconId);
        r.SpellFamilyName = spell.GetU(i, F_SpellFamilyName);
        r.SpellFamilyFlags = spell.GetU(i, F_SpellFamilyFlagsLo) | ((ulong)spell.GetU(i, F_SpellFamilyFlagsHi) << 32);

        for (int e = 0; e < 3; e++)
        {
            r.Effect[e]               = spell.GetU(i, F_Effect + e);
            r.EffectDieSides[e]       = spell.GetI(i, F_EffectDieSides + e);
            r.EffectBasePoints[e]     = spell.GetI(i, F_EffectBasePoints + e);
            r.EffectImplicitTargetA[e]= spell.GetU(i, F_EffectImplicitTargetA + e);
            r.EffectRadiusIndex[e]    = spell.GetU(i, F_EffectRadiusIndex + e);
            r.EffectApplyAuraName[e]  = spell.GetU(i, F_EffectApplyAuraName + e);
            r.EffectMiscValue[e]      = spell.GetI(i, F_EffectMiscValue + e);
            r.EffectTriggerSpell[e]   = spell.GetU(i, F_EffectTriggerSpell + e);
        }

        r.Name        = ReadStr(spell, i, F_SpellName);
        r.Rank        = ReadStr(spell, i, F_Rank);
        r.Description = ReadStr(spell, i, F_Description);
        r.ToolTip     = ReadStr(spell, i, F_ToolTip);

        r.IconPath = _iconPath.TryGetValue(r.SpellIconId, out var ip) ? ip : "";
        r.CastTimeMs = _castTime.TryGetValue(r.CastingTimeIndex, out var ct) ? ct : 0;
        r.DurationMs = _duration.TryGetValue(r.DurationIndex, out var du) ? du : 0;
        r.RangeMin = _rangeMin.TryGetValue(r.RangeIndex, out var rmin) ? rmin : 0;
        r.RangeMax = _rangeMax.TryGetValue(r.RangeIndex, out var rmax) ? rmax : 0;
    }

    public IReadOnlyList<(uint Id, string Path)> Icons
    {
        get
        {
            var l = new List<(uint, string)>(_iconPath.Count);
            foreach (var kv in _iconPath) if (kv.Value.Length > 0) l.Add((kv.Key, kv.Value));
            l.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return l;
        }
    }

    public uint NextFreeId()
    {
        uint max = 0;
        foreach (var s in _list) if (s.Id > max) max = s.Id;
        return max + 1;
    }

    public bool IdExists(uint id)
    {
        foreach (var s in _list) if (s.Id == id) return true;
        return false;
    }

    public SpellRecord AddSpell(SpellRecord template, uint newId, string name, string rank, string description, uint iconId)
    {
        var payload = (byte[])_spell.Records[template.RecIndex].Clone();
        int rec = _spell.AddRecord(payload);

        _spell.SetU(rec, 0, newId);
        _spell.SetU(rec, F_SpellIconId, iconId);
        _spell.SetU(rec, F_ActiveIconId, iconId);

        SetLocalizedString(rec, F_SpellName, name);
        SetLocalizedString(rec, F_Rank, rank);
        SetLocalizedString(rec, F_Description, description);

        var r = BuildSpellRecord(rec);
        _list.Add(r);
        return r;
    }

    private void SetLocalizedString(int rec, int firstField, string value)
    {
        _spell.SetU(rec, firstField, _spell.AddString(value));
        for (int k = 1; k < 8; k++) _spell.SetU(rec, firstField + k, 0);
    }

    public void SetIcon(SpellRecord spell, uint iconId)
    {
        _spell.SetU(spell.RecIndex, F_SpellIconId, iconId);
        _spell.SetU(spell.RecIndex, F_ActiveIconId, iconId);
        PopulateRecord(spell, spell.RecIndex);
    }

    public IEnumerable<SpellField> EnumerateFields(int recIndex)
    {
        var names = FieldNames;
        for (int f = 0; f < _spell.FieldCount; f++)
        {
            string name = f < names.Length ? names[f] : $"Field{f}";
            uint raw = _spell.GetU(recIndex, f);
            var kind = KindOf(name, f);
            string edit, display;
            if (kind == SpellFieldKind.Text)
            {
                string text = ReadStr(_spell, recIndex, f);
                edit = text;
                display = text.Length == 0 ? "(empty)" : "“" + text + "”";
            }
            else if (kind == SpellFieldKind.Float)
            {
                float fv = BitConverter.Int32BitsToSingle(unchecked((int)raw));
                edit = fv.ToString("0.######", CultureInfo.InvariantCulture);
                display = DecodeField(name, raw);
            }
            else
            {
                edit = unchecked((int)raw).ToString(CultureInfo.InvariantCulture);
                display = DecodeField(name, raw);
            }
            yield return new SpellField(name, f, kind, edit, display);
        }
    }

    public bool TrySetField(SpellRecord spell, int fieldIndex, SpellFieldKind kind, string input, out string error)
    {
        error = "";
        int rec = spell.RecIndex;
        try
        {
            switch (kind)
            {
                case SpellFieldKind.Text:
                    _spell.SetU(rec, fieldIndex, _spell.AddString(input ?? ""));
                    break;
                case SpellFieldKind.Float:
                    if (!float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                    { error = "Enter a number."; return false; }
                    _spell.SetF(rec, fieldIndex, fv);
                    break;
                default:
                    input = (input ?? "").Trim();
                    int iv;
                    if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!uint.TryParse(input.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uh))
                        { error = "Invalid hex value."; return false; }
                        iv = unchecked((int)uh);
                    }
                    else if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var siv)) iv = siv;
                    else if (uint.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uiv)) iv = unchecked((int)uiv);
                    else { error = "Enter an integer (or 0x... hex)."; return false; }
                    _spell.SetI(rec, fieldIndex, iv);
                    break;
            }
            PopulateRecord(spell, rec);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static SpellFieldKind KindOf(string name, int index)
    {
        if (index == F_SpellName || index == F_Rank || index == F_Description || index == F_ToolTip)
            return SpellFieldKind.Text;
        return IsFloatField(name) ? SpellFieldKind.Float : SpellFieldKind.Int;
    }

    private static bool IsFloatField(string name) =>
        name == "Speed" || name.StartsWith("EffectDicePerLevel") || name.StartsWith("EffectRealPointsPerLevel")
        || name.StartsWith("EffectMultipleValue") || name.StartsWith("EffectPointsPerComboPoint")
        || name.StartsWith("DmgMultiplier");

    private static readonly int[] KitEffectFields = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18 };

    public List<string> ResolveVisualModels(uint spellVisualId)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_spellVisual == null || spellVisualId == 0) return result;

        int row = _spellVisual.IndexOfId(spellVisualId);
        if (row < 0) return result;

        void Add(int effId)
        {
            if (effId <= 0 || !_effModel.TryGetValue((uint)effId, out var path) || path.Length == 0) return;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".mdx" or ".mdl" or ".m2")) return;
            string m2 = System.IO.Path.ChangeExtension(path, ".m2");
            if (seen.Add(m2)) result.Add(m2);
        }

        if (_spellVisual.FieldCount > 7) Add(_spellVisual.GetI(row, 7));

        if (_spellVisualKit != null)
            for (int f = 1; f <= 6 && f < _spellVisual.FieldCount; f++)
            {
                int kid = _spellVisual.GetI(row, f);
                if (kid <= 0) continue;
                int kr = _spellVisualKit.IndexOfId((uint)kid);
                if (kr < 0) continue;
                foreach (int ef in KitEffectFields)
                    if (ef < _spellVisualKit.FieldCount) Add(_spellVisualKit.GetI(kr, ef));
            }

        return result;
    }

    public string DecodeField(string name, uint raw)
    {
        int asInt = unchecked((int)raw);

        switch (name)
        {
            case "CastingTimeIndex":
                return _castTime.TryGetValue(raw, out var ct)
                    ? $"{asInt}  ({(ct <= 0 ? "Instant" : $"{ct / 1000.0:0.##}s")})" : asInt.ToString();
            case "DurationIndex":
                return _duration.TryGetValue(raw, out var du)
                    ? $"{asInt}  ({(du <= 0 ? "0" : du >= 0x7FFFFFF ? "∞" : FormatMs(du))})" : asInt.ToString();
            case "RangeIndex":
                return _rangeMax.TryGetValue(raw, out var rmax)
                    ? $"{asInt}  ({(_rangeMin.GetValueOrDefault(raw) > 0 ? $"{_rangeMin[raw]:0}-{rmax:0}" : $"{rmax:0}")} yd)" : asInt.ToString();
            case "SpellIconID":
            case "ActiveIconID":
                return _iconPath.TryGetValue(raw, out var ip) && ip.Length > 0 ? $"{asInt}  ({ShortIcon(ip)})" : asInt.ToString();
        }
        if (name.StartsWith("EffectRadiusIndex"))
            return raw != 0 && _radius.TryGetValue(raw, out var rad) ? $"{asInt}  ({rad:0.#} yd)" : asInt.ToString();

        switch (name)
        {
            case "School": return $"{asInt}  ({SchoolName(raw)})";
            case "PowerType": return $"{asInt}  ({PowerTypeName(raw)})";
            case "Dispel": return $"{asInt}  ({DispelName(raw)})";
            case "Mechanic": return raw == 0 ? "0" : $"{asInt}  ({MechanicName(raw)})";
        }
        if (name is "Effect1" or "Effect2" or "Effect3")
            return raw == 0 ? "0" : $"{asInt}  ({EffectName(raw)})";
        if (name.StartsWith("EffectApplyAuraName"))
            return raw == 0 ? "0" : $"{asInt}  ({AuraName(raw)})";
        if (name.StartsWith("EffectMechanic"))
            return raw == 0 ? "0" : $"{asInt}  ({MechanicName(raw)})";

        if (name == "Attributes") return FlagList(raw, Attr0Flags);
        if (name == "AttributesEx") return FlagList(raw, Attr1Flags);
        if (name is "AttributesEx2" or "AttributesEx3" or "AttributesEx4")
            return raw == 0 ? "0" : $"0x{raw:X8}";
        if (name == "Targets") return raw == 0 ? "0" : $"0x{raw:X}";

        if (IsFloatField(name))
        {
            float fv = BitConverter.Int32BitsToSingle(asInt);
            if (raw != 0 && !float.IsNaN(fv) && !float.IsInfinity(fv) && MathF.Abs(fv) < 1e9f)
                return $"≈{fv:0.###}";
        }
        return asInt.ToString();
    }

    private static string ReadStr(DbcFile dbc, int rec, int field)
    {
        uint ofs = dbc.GetU(rec, field);
        var block = dbc.StringBlock;
        if (ofs == 0 || ofs >= (uint)block.Length) return "";
        int end = (int)ofs;
        while (end < block.Length && block[end] != 0) end++;
        return Encoding.UTF8.GetString(block, (int)ofs, end - (int)ofs);
    }

    private static Dictionary<uint, string> LoadStringDbc(Func<string, Stream> open, string path, int strField)
    {
        var map = new Dictionary<uint, string>();
        try
        {
            DbcFile d; using (var s = open(path)) d = DbcFile.Read(s);
            for (int i = 0; i < d.RecordCount; i++) map[d.GetU(i, 0)] = ReadStr(d, i, strField);
        }
        catch { }
        return map;
    }

    private static Dictionary<uint, int> LoadIntDbc(Func<string, Stream> open, string path, int valField)
    {
        var map = new Dictionary<uint, int>();
        try
        {
            DbcFile d; using (var s = open(path)) d = DbcFile.Read(s);
            for (int i = 0; i < d.RecordCount; i++) map[d.GetU(i, 0)] = d.GetI(i, valField);
        }
        catch { }
        return map;
    }

    private static Dictionary<uint, float> LoadFloatDbc(Func<string, Stream> open, string path, int valField)
    {
        var map = new Dictionary<uint, float>();
        try
        {
            DbcFile d; using (var s = open(path)) d = DbcFile.Read(s);
            for (int i = 0; i < d.RecordCount; i++) map[d.GetU(i, 0)] = d.GetF(i, valField);
        }
        catch { }
        return map;
    }

    private static (Dictionary<uint, float> Min, Dictionary<uint, float> Max) LoadRangeDbc(Func<string, Stream> open)
    {
        var min = new Dictionary<uint, float>(); var max = new Dictionary<uint, float>();
        try
        {
            DbcFile d; using (var s = open(@"DBFilesClient\SpellRange.dbc")) d = DbcFile.Read(s);
            for (int i = 0; i < d.RecordCount; i++) { uint id = d.GetU(i, 0); min[id] = d.GetF(i, 1); max[id] = d.GetF(i, 2); }
        }
        catch { }
        return (min, max);
    }

    private static string[]? _fieldNames;
    private static string[] FieldNames => _fieldNames ??= BuildFieldNames();

    private static string[] BuildFieldNames()
    {
        var n = new string[173];
        void Set(int i, string s) => n[i] = s;
        void Triple(int start, string s) { for (int e = 0; e < 3; e++) n[start + e] = $"{s}{e + 1}"; }
        void Eight(int start, string s) { for (int e = 0; e < 8; e++) n[start + e] = $"{s}{e + 1}"; }

        Set(0,"Id"); Set(1,"School"); Set(2,"Category"); Set(3,"CastUI"); Set(4,"Dispel"); Set(5,"Mechanic");
        Set(6,"Attributes"); Set(7,"AttributesEx"); Set(8,"AttributesEx2"); Set(9,"AttributesEx3"); Set(10,"AttributesEx4");
        Set(11,"Stances"); Set(12,"StancesNot"); Set(13,"Targets"); Set(14,"TargetCreatureType"); Set(15,"RequiresSpellFocus");
        Set(16,"CasterAuraState"); Set(17,"TargetAuraState"); Set(18,"CastingTimeIndex"); Set(19,"RecoveryTime"); Set(20,"CategoryRecoveryTime");
        Set(21,"InterruptFlags"); Set(22,"AuraInterruptFlags"); Set(23,"ChannelInterruptFlags"); Set(24,"ProcFlags"); Set(25,"ProcChance"); Set(26,"ProcCharges");
        Set(27,"MaxLevel"); Set(28,"BaseLevel"); Set(29,"SpellLevel"); Set(30,"DurationIndex"); Set(31,"PowerType");
        Set(32,"ManaCost"); Set(33,"ManaCostPerLevel"); Set(34,"ManaPerSecond"); Set(35,"ManaPerSecondPerLevel"); Set(36,"RangeIndex"); Set(37,"Speed");
        Set(38,"ModalNextSpell"); Set(39,"StackAmount"); Set(40,"Totem1"); Set(41,"Totem2");
        Eight(42,"Reagent"); Eight(50,"ReagentCount");
        Set(58,"EquippedItemClass"); Set(59,"EquippedItemSubClassMask"); Set(60,"EquippedItemInventoryTypeMask");
        Triple(61,"Effect"); Triple(64,"EffectDieSides"); Triple(67,"EffectBaseDice"); Triple(70,"EffectDicePerLevel");
        Triple(73,"EffectRealPointsPerLevel"); Triple(76,"EffectBasePoints"); Triple(79,"EffectMechanic");
        Triple(82,"EffectImplicitTargetA"); Triple(85,"EffectImplicitTargetB"); Triple(88,"EffectRadiusIndex");
        Triple(91,"EffectApplyAuraName"); Triple(94,"EffectAmplitude"); Triple(97,"EffectMultipleValue");
        Triple(100,"EffectChainTarget"); Triple(103,"EffectItemType"); Triple(106,"EffectMiscValue");
        Triple(109,"EffectTriggerSpell"); Triple(112,"EffectPointsPerComboPoint");
        Set(115,"SpellVisual"); Set(116,"SpellVisual2"); Set(117,"SpellIconID"); Set(118,"ActiveIconID"); Set(119,"SpellPriority");
        Set(120,"SpellName"); for (int k = 1; k < 8; k++) Set(120 + k, $"SpellName_loc{k + 1}"); Set(128,"SpellNameFlags");
        Set(129,"Rank"); for (int k = 1; k < 8; k++) Set(129 + k, $"Rank_loc{k + 1}"); Set(137,"RankFlags");
        Set(138,"Description"); for (int k = 1; k < 8; k++) Set(138 + k, $"Description_loc{k + 1}"); Set(146,"DescriptionFlags");
        Set(147,"ToolTip"); for (int k = 1; k < 8; k++) Set(147 + k, $"ToolTip_loc{k + 1}"); Set(155,"ToolTipFlags");
        Set(156,"ManaCostPercentage"); Set(157,"StartRecoveryCategory"); Set(158,"StartRecoveryTime"); Set(159,"MaxTargetLevel");
        Set(160,"SpellFamilyName"); Set(161,"SpellFamilyFlags_lo"); Set(162,"SpellFamilyFlags_hi");
        Set(163,"MaxAffectedTargets"); Set(164,"DmgClass"); Set(165,"PreventionType"); Set(166,"StanceBarOrder");
        Triple(167,"DmgMultiplier"); Set(170,"MinFactionId"); Set(171,"MinReputation"); Set(172,"RequiredAuraVision");

        for (int i = 0; i < n.Length; i++) n[i] ??= $"Field{i}";
        return n;
    }

    public static string SchoolName(uint s) => s switch
    { 0 => "Physical", 1 => "Holy", 2 => "Fire", 3 => "Nature", 4 => "Frost", 5 => "Shadow", 6 => "Arcane", _ => $"School {s}" };

    public static string PowerTypeName(uint p) => p switch
    { 0 => "Mana", 1 => "Rage", 2 => "Focus", 3 => "Energy", 4 => "Happiness", _ => $"Power {p}" };

    public static string EffectName(uint e) => e switch
    {
        0 => "(none)", 1 => "Instakill", 2 => "School Damage", 3 => "Dummy", 5 => "Teleport Units (old)",
        6 => "Apply Aura", 7 => "Environmental Damage", 8 => "Power Drain", 9 => "Health Leech", 10 => "Heal",
        11 => "Bind", 12 => "Portal", 16 => "Quest Complete", 17 => "Weapon Damage (no school)",
        18 => "Resurrect", 19 => "Add Extra Attacks", 20 => "Dodge", 24 => "Create Item", 26 => "Open Lock",
        27 => "Persistent Area Aura", 28 => "Summon", 29 => "Leap", 30 => "Energize", 31 => "Weapon % Damage",
        32 => "Teleport Units", 33 => "Trigger Missile", 36 => "Learn Spell", 38 => "Dispel",
        42 => "Trigger Spell (old)", 44 => "Skill Step", 48 => "Stealth", 53 => "Enchant Item",
        54 => "Enchant Item Temporary", 56 => "Summon Pet", 58 => "Weapon Damage", 62 => "Interrupt Cast",
        64 => "Trigger Spell", 77 => "Script Effect", 80 => "Stuck", 92 => "Pull", 99 => "Attack Me",
        _ => $"Effect {e}",
    };

    public static string DispelName(uint d) => d switch
    {
        0 => "None", 1 => "Magic", 2 => "Curse", 3 => "Disease", 4 => "Poison",
        5 => "Stealth", 6 => "Invisibility", 7 => "All", 9 => "Enrage", _ => $"Dispel {d}",
    };

    public static string MechanicName(uint m) => m switch
    {
        0 => "None", 1 => "Charm", 2 => "Disoriented", 3 => "Disarm", 4 => "Distract", 5 => "Flee",
        6 => "Grip", 7 => "Root", 8 => "Slow Attack", 9 => "Silence", 10 => "Sleep", 11 => "Snare",
        12 => "Stun", 13 => "Freeze", 14 => "Knockout", 15 => "Bleed", 16 => "Bandage", 17 => "Polymorph",
        18 => "Banish", 19 => "Shield", 20 => "Shackle", 21 => "Mount", 22 => "Persuade (Infected)",
        23 => "Turn", 24 => "Horror", 25 => "Invulnerability", 26 => "Interrupt", 27 => "Daze",
        28 => "Discovery", 29 => "Immune Shield", 30 => "Sapped", _ => $"Mechanic {m}",
    };

    public static string AuraName(uint a) => a switch
    {
        0 => "None", 1 => "Bind Sight", 2 => "Mod Possess", 3 => "Periodic Damage", 4 => "Dummy",
        5 => "Mod Confuse", 6 => "Mod Charm", 7 => "Mod Fear", 8 => "Periodic Heal", 9 => "Mod Attack Speed",
        10 => "Mod Threat", 11 => "Mod Taunt", 12 => "Mod Stun", 13 => "Mod Damage Done",
        14 => "Mod Damage Taken", 15 => "Damage Shield", 16 => "Mod Stealth", 17 => "Mod Stealth Detect",
        18 => "Mod Invisibility", 20 => "Periodic Heal %", 22 => "Mod Resistance", 23 => "Periodic Trigger Spell",
        24 => "Periodic Energize", 25 => "Mod Pacify", 26 => "Mod Root", 27 => "Mod Silence",
        29 => "Mod Stat", 30 => "Mod Skill", 31 => "Mod Increase Speed", 33 => "Mod Decrease Speed",
        34 => "Mod Increase Health", 35 => "Mod Increase Energy", 36 => "Mod Shapeshift",
        41 => "Mod Invisibility Detect", 56 => "Transform", 65 => "Mod Casting Speed",
        69 => "School Absorb", 77 => "Mechanic Immunity", 78 => "Mounted", 79 => "Mod Damage %",
        99 => "Mod Attack Power", 101 => "Mod Resistance %", 107 => "Add Flat Modifier",
        108 => "Add Pct Modifier", 118 => "Mod Healing %", _ => $"Aura {a}",
    };

    private static readonly (uint Bit, string Name)[] Attr0Flags =
    {
        (0x00000002, "Ranged"), (0x00000004, "OnNextSwing"), (0x00000008, "IsReplenishment"),
        (0x00000010, "Ability"), (0x00000020, "TradeSpell"), (0x00000040, "Passive"),
        (0x00000080, "HiddenClientside"), (0x00000100, "HideInCombatLog"), (0x00000200, "TargetMainhandItem"),
        (0x00000400, "OnNextSwing2"), (0x00001000, "DaytimeOnly"), (0x00002000, "NightOnly"),
        (0x00004000, "IndoorsOnly"), (0x00008000, "OutdoorsOnly"), (0x00010000, "NotWhileShapeshifted"),
        (0x00020000, "OnlyStealthed"), (0x00040000, "DontAffectSheath"), (0x00080000, "ScalesWithLevel"),
        (0x00100000, "StopAttackTarget"), (0x00200000, "NoDodgeParryBlock"), (0x00400000, "CastTrackTarget"),
        (0x00800000, "CastableWhileDead"), (0x01000000, "CastableWhileMounted"), (0x02000000, "DisabledWhileActive"),
        (0x04000000, "Negative"), (0x08000000, "CastableWhileSitting"), (0x10000000, "CantUseInCombat"),
        (0x20000000, "UnaffectedByInvuln"), (0x40000000, "BreakableByDamage"), (0x80000000, "CantCancel"),
    };

    private static readonly (uint Bit, string Name)[] Attr1Flags =
    {
        (0x00000002, "DrainAllPower"), (0x00000004, "Channeled"), (0x00000008, "CantBeRedirected"),
        (0x00000020, "NotBreakStealth"), (0x00000040, "Channeled2"), (0x00000080, "CantBeReflected"),
        (0x00000100, "TargetNotInCombat"), (0x00000400, "MeleeCombatStart"), (0x00008000, "RemoveOnImmunity"),
        (0x00100000, "ReqComboPoints1"), (0x00400000, "ReqComboPoints2"), (0x04000000, "IsFishing"),
    };

    private static string FlagList(uint raw, (uint Bit, string Name)[] table)
    {
        if (raw == 0) return "0";
        var named = new List<string>();
        uint leftover = raw;
        foreach (var (bit, name) in table)
            if ((raw & bit) != 0) { named.Add(name); leftover &= ~bit; }
        string head = $"0x{raw:X8}";
        if (named.Count == 0) return head;
        string s = head + "  (" + string.Join(", ", named) + ")";
        if (leftover != 0) s += $" +0x{leftover:X}";
        return s;
    }

    private static string FormatMs(int ms) => ms % 1000 == 0 ? $"{ms / 1000}s" : $"{ms / 1000.0:0.#}s";

    private static string ShortIcon(string path)
    {
        int slash = path.LastIndexOf('\\');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
