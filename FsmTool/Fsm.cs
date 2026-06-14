using System.Globalization;
using System.IO;
using System.Text;

namespace FsmTool;

// ---------------------------------------------------------------------------
//    KH3D @FSM v3 parser - Finite State Machine
//    m_di020.fsm. Layout:
//    header : "@FSM" | u16 version | u16 recordCount | u32 dataOffset
//    symtab : [32-byte name][u32 id]  repeated, from offset 12 to dataOffset
//    records: 80 bytes each, from offset 800
//             +4 u16 index | +8 u32 transitionEntryPtr | +12 u32 ownerNameOff
//    entry  : u32 targetPtr | u16 tag | u16 condCount | u32 detailPtr
//    detail : u32 condNameOff | u32 op | u32 param      (12 bytes, x condCount)
// ---------------------------------------------------------------------------

public sealed class Condition
{
    public string Name { get; init; } = "";
    public uint Op { get; init; }
    public uint Raw { get; init; }
    public int ParamOffset { get; init; }   // file offset of the 4-byte param
    public int OpOffset => ParamOffset - 4;  // the 4-byte op sits just before the value
    public int NameOffset => ParamOffset - 8; // u32 condition-name pointer (detail + 0)
    public string Display { get; init; } = "";
    // operator glyph for the UI (mirrors the parser Ops table)
    public string OpText => Op switch { 0x21 => "is", 1 => "<", 3 => "<=", 4 => "<", 5 => ">", 6 => ">=", _ => "?" };

    // "bool" | "percent" | "float" | "int" — drives which editor the UI shows
    public string Kind { get; init; } = "float";
    public bool IsBool => Kind == "bool";
    public bool IsNumber => !IsBool;          // int/float/percent all use the number box
    public bool BoolValue => Raw != 0;
    public float FloatValue => BitConverter.UInt32BitsToSingle(Raw);
    // string shown in the number box (integer for int kind, else decimal)
    public string ValueText => Kind == "int"
        ? Raw.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : FloatValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class Transition
{
    public string Owner { get; init; } = "";
    public string Target { get; init; } = "";
    public List<Condition> Conditions { get; init; } = [];

    public int EntryOffset { get; init; }    // p8; target pointer is its first 4 bytes
    public uint TargetPtr { get; init; }

    public bool IsAttack => Target.StartsWith("Attack") || Target.StartsWith("Magic");
    public string When => Conditions.Count == 0
        ? "(always)"
        : string.Join("  AND  ", Conditions.Select(c => c.Display));

    public string EdgeText => $"{Owner}  \u2192  {Target}";
    public string OffsetText => $"entry @ 0x{EntryOffset:X4}";
    public string ArrowText => $"--[ {When} ]-->  {Target}";
}

public sealed class FsmState
{
    public string Name { get; init; } = "";
    public List<Transition> Transitions { get; init; } = [];
    public bool HasTransitions => Transitions.Count > 0;
}

public sealed class FsmDocument
{
    public string Path { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public int Version { get; set; }
    public int ByteLength { get; set; }

    public List<string> States { get; set; } = [];
    public List<string> Conditions { get; set; } = [];
    public List<FsmState> Graph { get; set; } = [];
    public List<Transition> AttackEdges { get; set; } = [];

    // every transition across all states, flattened (drives the editor list)
    public List<Transition> AllTransitions => [.. Graph.SelectMany(s => s.Transitions)];
    public uint IdleTargetValue { get; set; }

    // state name -> the pointer value a transition target must hold to mean that
    // state (the owner-field offset of that state's first record). Drives the
    // retarget dropdown; every entry is a valid, in-place target.
    public Dictionary<string, uint> StateTargets { get; set; } = [];
    // sorted names for the UI dropdown
    public List<string> StateOptions => [.. StateTargets.Keys.OrderBy(s => s)];
    // condition variable name -> its symbol-table offset. Writing this into a
    // condition's name pointer makes it test that variable (size-preserving).
    public Dictionary<string, uint> ConditionSymbols { get; set; } = [];
    public List<string> ConditionOptions => [.. Conditions.OrderBy(s => s)];
    public bool HasBackup => File.Exists(Path + ".bak");

    public string Title => $"{System.IO.Path.GetFileName(Path)}   (@FSM v{Version}, {ByteLength:N0} bytes)";
    public string StatesLine => $"States ({States.Count}): {string.Join(", ", States)}";
    public string ConditionsLine => $"Conditions ({Conditions.Count}): {string.Join(", ", Conditions)}";

    // all size-preserving (overwrite 4 bytes), so no pointer
    // ever moves and the file can't be structurally corrupted.
    // Each saves with a one-time .bak.

    // Repoint a transition to any state by name. Returns false if the name has
    // no record to point at (target-only state) — caller should ignore.
    public bool Retarget(Transition t, string stateName)
    {
        if (!StateTargets.TryGetValue(stateName, out uint v)) return false;
        PatchU32(t.EntryOffset, v);
        SaveWithBackup();
        return true;
    }

    // Set a condition's flag value (true/false).
    public void SetConditionBool(Condition c, bool on)
    {
        PatchU32(c.ParamOffset, on ? 1u : 0u);
        SaveWithBackup();
    }

    // Set a condition's numeric value (time/distance/threshold/percent), stored
    // as a 32-bit float.
    public void SetConditionFloat(Condition c, float v)
    {
        PatchU32(c.ParamOffset, BitConverter.SingleToUInt32Bits(v));
        SaveWithBackup();
    }

    // Set a condition's integer value (small counts/enums stored as a raw int).
    public void SetConditionInt(Condition c, uint v)
    {
        PatchU32(c.ParamOffset, v);
        SaveWithBackup();
    }

    // Set a condition's comparison operator (raw code).
    public void SetConditionOp(Condition c, uint op)
    {
        PatchU32(c.OpOffset, op);
        SaveWithBackup();
    }

    // Repoint a condition at a different variable by name (size-preserving).
    // op/value bytes are left untouched — adjust them after if the new variable
    // wants a different shape (e.g. a flag vs a threshold).
    public bool SetConditionName(Condition c, string name)
    {
        if (!ConditionSymbols.TryGetValue(name, out uint off)) return false;
        PatchU32(c.NameOffset, off);
        SaveWithBackup();
        return true;
    }

    // operator glyphs offered in the UI, and the canonical code each writes.
    private static readonly Dictionary<string, uint> OpCodes = new()
    { { "is", 0x21 }, { "<", 1 }, { "<=", 3 }, { ">", 5 }, { ">=", 6 } };
    public IReadOnlyList<string> OperatorOptions { get; } = ["is", "<", "<=", ">", ">="];

    public bool SetConditionOpText(Condition c, string glyph)
    {
        if (!OpCodes.TryGetValue(glyph, out uint op)) return false;
        SetConditionOp(c, op);
        return true;
    }

    // Restore the original file from its .bak (one-click undo of every change).
    public bool RevertToBackup()
    {
        string bak = Path + ".bak";
        if (!File.Exists(bak)) return false;
        File.Copy(bak, Path, overwrite: true);
        Bytes = File.ReadAllBytes(Path);
        return true;
    }

    // Write the current (possibly edited) bytes to a new file, leaving the
    // original/donor untouched. Use this to make a custom ally from a donor FSM.
    public void SaveAsCopy(string destPath)
        => File.WriteAllBytes(destPath, Bytes);

    // Redirect one transition's target pointer to an Idle node and persist
    // (writing a one-time .bak alongside the original).
    public void RedirectToIdle(Transition t)
    {
        PatchU32(t.EntryOffset, IdleTargetValue);
        SaveWithBackup();
    }

    // Redirect every transition that does not already point at Idle, then save
    // once. Returns the number of edges changed. This pins the entity in the
    // Idle state (pure follow), removing all native behaviour.
    public int RedirectAllToIdle()
    {
        int n = 0;
        foreach (var st in Graph)
            foreach (var t in st.Transitions)
                if (t.Target != "Idle")
                {
                    PatchU32(t.EntryOffset, IdleTargetValue);
                    n++;
                }
        if (n > 0) SaveWithBackup();
        return n;
    }

    private void PatchU32(int o, uint v)
    {
        Bytes[o + 0] = (byte)(v & 0xFF);
        Bytes[o + 1] = (byte)((v >> 8) & 0xFF);
        Bytes[o + 2] = (byte)((v >> 16) & 0xFF);
        Bytes[o + 3] = (byte)((v >> 24) & 0xFF);
    }

    private void SaveWithBackup()
    {
        string bak = Path + ".bak";
        if (!File.Exists(bak))
            File.Copy(Path, bak);
        File.WriteAllBytes(Path, Bytes);
    }

    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 70));
        sb.AppendLine(" " + Title);
        sb.AppendLine(new string('=', 70));
        sb.AppendLine(" " + StatesLine);
        sb.AppendLine(" " + ConditionsLine);
        sb.AppendLine();
        sb.AppendLine(" STATE GRAPH   (state --[ when ]--> next state)");
        sb.AppendLine(new string('-', 70));
        foreach (var st in Graph)
        {
            sb.AppendLine($" [{st.Name}]");
            if (!st.HasTransitions) { sb.AppendLine("     (no outgoing transitions)"); }
            foreach (var tr in st.Transitions)
                sb.AppendLine($"     {tr.ArrowText}{(tr.IsAttack ? "   <== ATTACK" : "")}");
            sb.AppendLine();
        }
        sb.AppendLine(new string('-', 70));
        sb.AppendLine(" ATTACK ENTRIES (target Attack*/Magic*)");
        sb.AppendLine(new string('-', 70));
        foreach (var tr in AttackEdges)
        {
            sb.AppendLine($" {tr.Owner} -> {tr.Target}   when {tr.When}");
            sb.AppendLine($"     {tr.OffsetText}  target ptr 0x{tr.TargetPtr:X8}" +
                          $"  -> redirect to Idle = 0x{IdleTargetValue:X8}");
        }
        return sb.ToString();
    }
}

public static class Fsm
{
    // Display ordering only — known states are shown in this reading order; any
    // state names NOT in this list (e.g. from other entities' larger .fsm files)
    // are appended afterwards in record order. Classification does NOT depend on
    // this list: a symbol is a STATE if it is used as a record owner or as a
    // transition target, otherwise it is a CONDITION.
    private static readonly string[] PreferredOrder =
    [
        "Appear","Idle","TargetSearch","Pursuit","MoveToSpecifiedPos",
        "Attack1","Magic1","Action1","Evade","Wander","Blank"
    ];

    // best-effort comparison-operator labels
    private static readonly Dictionary<uint, string> Ops = new()
    {
        { 0x21, "is" }, { 1, "<" }, { 3, "<=" }, { 4, "<" }, { 5, ">" }, { 6, ">=" }
    };

    private static readonly HashSet<string> Percent = ["Probability", "CompanionHealth", "MyHealth"];

    // Comparison operators carry a numeric (float) threshold; "is" (0x21) and the
    // like with a 0/1 value are boolean flags. This is more reliable than keeping
    // a name whitelist, and generalises to conditions we've never seen.
    private static bool IsCompareOp(uint op) => op is 1 or 3 or 4 or 5 or 6;

    private static string KindOf(string cond, uint op, uint raw)
    {
        if (Percent.Contains(cond)) return "percent";
        // compare ops carry a numeric threshold: a real float has a large bit
        // pattern; a small raw value is an integer count/enum (e.g. Counter < 1).
        if (IsCompareOp(op)) return raw < 0x10000 ? "int" : "float";
        return raw <= 1 ? "bool" : "float";   // "is 0/1" => flag
    }

    private static ushort U16(byte[] d, int o) => (ushort)(d[o] | d[o + 1] << 8);
    private static uint U32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
    private static float F32(byte[] d, int o) => BitConverter.UInt32BitsToSingle(U32(d, o));

    private static string FmtParam(string cond, uint op, uint raw)
    {
        string kind = KindOf(cond, op, raw);
        if (kind == "bool") return raw != 0 ? "true" : "false";
        if (kind == "int") return raw.ToString(CultureInfo.InvariantCulture);
        float f = BitConverter.UInt32BitsToSingle(raw);
        if (kind == "percent") return f.ToString("0.###", CultureInfo.InvariantCulture) + "%";
        return f.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string CondString(string cond, uint op, uint raw)
    {
        string o = Ops.TryGetValue(op, out var s) ? s : $"op{op}";
        return $"{cond} {o} {FmtParam(cond, op, raw)}";
    }

    public static FsmDocument Parse(string path)
        => Parse(File.ReadAllBytes(path), path);

    public static FsmDocument Parse(byte[] d, string path)
    {
        if (d.Length < 12 || d[0] != (byte)'@' || d[1] != (byte)'F' || d[2] != (byte)'S' || d[3] != (byte)'M')
            throw new InvalidDataException("Not an @FSM file.");

        var doc = new FsmDocument { Path = path, Bytes = d, ByteLength = d.Length, Version = U16(d, 4) };
        int dataOff = (int)U32(d, 8);

        // ---- symbol table (read only; do NOT classify yet) ----
        const int SYM = 12;
        int nsym = (dataOff - SYM) / 36;
        var sym = new Dictionary<int, string>();   // name-offset -> name
        var symByName = new Dictionary<string, uint>(); // name -> first name-offset
        List<string> symOrder = [];          // symtab order, for stable condition listing
        for (int i = 0; i < nsym; i++)
        {
            int b = SYM + i * 36;
            int end = Array.IndexOf(d, (byte)0, b, 32);
            if (end < 0) end = b + 32;
            string name = Encoding.ASCII.GetString(d, b, end - b);
            sym[b] = name;
            symOrder.Add(name);
            if (!symByName.ContainsKey(name)) symByName[name] = (uint)b;
        }

        // ---- locate the record table (80-byte records) ----
        // find the longest run of consecutive records whose owner field (+12) is
        // a valid symbol offset, scanning forward from the end of the symbol table.
        // Generalises to any file size / symbol count.
        const int ST = 80;
        int recStart = FindRecordTable(d, dataOff, sym, ST, out int recCount);
        if (recCount == 0)
            throw new InvalidDataException("No record table found (no run of valid 80-byte records).");

        var records = new List<(int idx, int p8, string owner, int off)>(recCount);
        for (int i = 0; i < recCount; i++)
        {
            int b = recStart + i * ST;
            int owner = (int)U32(d, b + 12);
            records.Add((U16(d, b + 4), (int)U32(d, b + 8), sym[owner], b));
        }

        // idle redirect target = owner-field offset of the first Idle record
        foreach (var (_, _, owner, off) in records)
            if (owner == "Idle") { doc.IdleTargetValue = (uint)(off + 12); break; }

        // retarget map: state name -> owner-field offset of its first record.
        // A transition target pointer holding this value resolves to that state.
        foreach (var (_, _, owner, off) in records)
            if (!doc.StateTargets.ContainsKey(owner))
                doc.StateTargets[owner] = (uint)(off + 12);

        // ---- resolve transitions + gather structural classification sets ----
        HashSet<string> ownerSet = [];
        HashSet<string> targetSet = [];
        var graphMap = new Dictionary<string, FsmState>();
        List<FsmState> graphOrder = [];
        FsmState StateFor(string name)
        {
            if (!graphMap.TryGetValue(name, out var s))
            {
                s = new FsmState { Name = name };
                graphMap[name] = s;
                graphOrder.Add(s);
            }
            return s;
        }

        foreach (var (idx, p8, owner, off) in records)
        {
            ownerSet.Add(owner);
            var state = StateFor(owner);
            if (p8 == 0 || p8 + 12 > d.Length) continue;

            uint targetPtr = U32(d, p8);
            int condCount = U16(d, p8 + 6);
            int detailPtr = (int)U32(d, p8 + 8);

            string target;
            if (targetPtr + 4 <= d.Length && sym.TryGetValue((int)U32(d, (int)targetPtr), out var tn))
            {
                target = tn;
                targetSet.Add(tn);
            }
            else target = $"?0x{targetPtr:X}";

            List<Condition> conds = [];
            for (int k = 0; k < condCount; k++)
            {
                int e = detailPtr + k * 12;
                if (detailPtr == 0 || e + 12 > d.Length) break;
                string cname = sym.TryGetValue((int)U32(d, e), out var cn) ? cn : $"?0x{U32(d, e):X}";
                uint op = U32(d, e + 4);
                uint raw = U32(d, e + 8);
                conds.Add(new Condition
                {
                    Name = cname,
                    Op = op,
                    Raw = raw,
                    ParamOffset = e + 8,
                    Display = CondString(cname, op, raw),
                    Kind = KindOf(cname, op, raw)
                });
            }

            var tr = new Transition
            {
                Owner = owner,
                Target = target,
                Conditions = conds,
                EntryOffset = p8,
                TargetPtr = targetPtr
            };
            state.Transitions.Add(tr);
            if (tr.IsAttack) doc.AttackEdges.Add(tr);
        }

        // ---- classify: state = owner|target, everything else = condition ----
        HashSet<string> stateSet = [.. ownerSet];
        stateSet.UnionWith(targetSet);
        // states listed in preferred reading order, unknowns appended in record order
        doc.States = [.. PreferredOrder.Where(stateSet.Contains)
, .. graphOrder.Select(g => g.Name).Where(n => !PreferredOrder.Contains(n))];
        // conditions = symbols that are not states, kept in symbol-table order, de-duped
        HashSet<string> seen = [];
        doc.Conditions = [.. symOrder.Where(n => !stateSet.Contains(n) && seen.Add(n))];
        foreach (var n in doc.Conditions)
            if (symByName.TryGetValue(n, out var o)) doc.ConditionSymbols[n] = o;

        // ---- graph node order: preferred names first, then remaining owners ----
        List<FsmState> ordered = [];
        foreach (var nm in PreferredOrder)
            if (graphMap.TryGetValue(nm, out var s)) ordered.Add(s);
        foreach (var s in graphOrder)
            if (!ordered.Contains(s)) ordered.Add(s);
        doc.Graph = ordered;

        return doc;
    }

    // Longest run of consecutive 80-byte records, starting at or after the end
    // of the symbol table, whose owner field (+12) is a valid symbol offset.
    private static int FindRecordTable(byte[] d, int dataOff, Dictionary<int, string> sym, int ST, out int count)
    {
        int bestStart = -1, bestCount = 0;
        for (int o = Math.Max(12, dataOff); o + 20 <= d.Length; o += 4)
        {
            int n = 0, p = o;
            while (p + 20 <= d.Length && sym.ContainsKey((int)U32(d, p + 12))) { n++; p += ST; }
            if (n > bestCount) { bestCount = n; bestStart = o; }
        }
        count = bestCount;
        return bestStart < 0 ? dataOff : bestStart;
    }
}