using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds a ball-and-stick caffeine molecule (C8H10N4O2, 24 atoms, 25 bonds) in code.
/// Coordinates: planar purine skeleton (idealised bond lengths, Angstrom) with methyl
/// hydrogens lifted out of plane in a tetrahedral arrangement. Centred on the origin.
/// </summary>
public class MoleculeBuilder : MonoBehaviour
{
    public Material carbon, hydrogen, nitrogen, oxygen, bond;

    [Tooltip("Visual radius = van der Waals radius * this")]
    public float radiusScale = 0.2f;
    public float bondRadius = 0.075f;
    [Tooltip("Pick collider is this many times larger than the visible atom")]
    public float pickScale = 1.6f;

    public const string AtomLayerName = "Default";

    struct Atom { public string name; public char el; public Vector3 pos; }

    readonly List<Atom> atoms = new List<Atom>();
    readonly Dictionary<string, int> index = new Dictionary<string, int>();
    readonly List<(int a, int b)> bonds = new List<(int, int)>();

    public int AtomCount => atoms.Count;
    public int BondCount => bonds.Count;
    public string Formula
    {
        get
        {
            int c = 0, h = 0, n = 0, o = 0;
            foreach (var a in atoms) { if (a.el == 'C') c++; else if (a.el == 'H') h++; else if (a.el == 'N') n++; else o++; }
            return $"C{c}H{h}N{n}O{o}";
        }
    }

    void Awake() { Build(); }

    int Add(string name, char el, Vector3 p)
    {
        atoms.Add(new Atom { name = name, el = el, pos = p });
        index[name] = atoms.Count - 1;
        return atoms.Count - 1;
    }

    void Bond(string a, string b) { bonds.Add((index[a], index[b])); }

    // Exocyclic substituent: from `from` along unit direction `dir` for `len`.
    static Vector3 Out(Vector3 from, Vector3 dir, float len) => from + dir.normalized * len;

    void AddMethyl(string tag, string nName, Vector3 nPos, Vector3 dir)
    {
        Vector3 c = Out(nPos, dir, 1.46f);
        Add("C" + tag, 'C', c);
        Bond(nName, "C" + tag);
        Vector3 d = dir.normalized;
        Vector3 p = new Vector3(-d.y, d.x, 0f);   // in-plane perpendicular
        Vector3 z = Vector3.forward;               // out-of-plane
        float[] phi = { 90f, 210f, 330f };
        for (int i = 0; i < 3; i++)
        {
            float r = phi[i] * Mathf.Deg2Rad;
            Vector3 perp = p * Mathf.Cos(r) + z * Mathf.Sin(r);
            Vector3 h = c + 1.09f * (d * 0.334f + perp * 0.943f);   // tetrahedral, 70.5 deg to bond axis
            Add($"H{tag}{i + 1}", 'H', h);
            Bond("C" + tag, $"H{tag}{i + 1}");
        }
    }

    public void Build()
    {
        // Six-membered ring (hexagon, r = 1.4) centred on origin.
        const float r6 = 1.4f;
        Vector3 Hex(float deg) => new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad), 0f) * r6;
        Vector3 c5 = Hex(30), c6 = Hex(90), n1 = Hex(150), c2 = Hex(210), n3 = Hex(270), c4 = Hex(330);

        // Five-membered ring fused on the C4-C5 bond, extending to +x.
        Vector3 centre5 = new Vector3(c4.x + 1.4f / (2f * Mathf.Tan(36f * Mathf.Deg2Rad)), 0f, 0f);
        float r5 = 1.4f / (2f * Mathf.Sin(36f * Mathf.Deg2Rad));
        Vector3 Pent(float deg) => centre5 + new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad), 0f) * r5;
        Vector3 n7 = Pent(72), c8 = Pent(0), n9 = Pent(-72);

        Add("N1", 'N', n1); Add("C2", 'C', c2); Add("N3", 'N', n3);
        Add("C4", 'C', c4); Add("C5", 'C', c5); Add("C6", 'C', c6);
        Add("N7", 'N', n7); Add("C8", 'C', c8); Add("N9", 'N', n9);

        Bond("N1", "C2"); Bond("C2", "N3"); Bond("N3", "C4"); Bond("C4", "C5"); Bond("C5", "C6"); Bond("C6", "N1");
        Bond("C4", "N9"); Bond("N9", "C8"); Bond("C8", "N7"); Bond("N7", "C5");

        // Carbonyl oxygens.
        Add("O2", 'O', Out(c2, c2, 1.22f)); Bond("C2", "O2");
        Add("O6", 'O', Out(c6, c6, 1.22f)); Bond("C6", "O6");

        // Hydrogen on C8.
        Add("H8", 'H', Out(c8, Vector3.right, 1.08f)); Bond("C8", "H8");

        // Methyl groups on N1, N3, N7.
        AddMethyl("M1", "N1", n1, n1);
        AddMethyl("M3", "N3", n3, n3);
        AddMethyl("M7", "N7", n7, n7 - centre5);

        // Centre on origin.
        Vector3 centroid = Vector3.zero;
        foreach (var a in atoms) centroid += a.pos;
        centroid /= atoms.Count;
        for (int i = 0; i < atoms.Count; i++) { var a = atoms[i]; a.pos -= centroid; atoms[i] = a; }

        // Visuals.
        foreach (var a in atoms)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Atom_" + a.name;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = a.pos;
            float rad = Radius(a.el) * radiusScale;
            go.transform.localScale = Vector3.one * rad * 2f;
            go.GetComponent<MeshRenderer>().sharedMaterial = MaterialFor(a.el);
            var col = go.GetComponent<SphereCollider>();
            col.radius = 0.5f * pickScale;   // invisible, slightly larger pick volume
        }

        foreach (var (ia, ib) in bonds)
        {
            Vector3 pa = atoms[ia].pos, pb = atoms[ib].pos, mid = (pa + pb) * 0.5f;
            MakeStick(pa, mid, atoms[ia].el);
            MakeStick(mid, pb, atoms[ib].el);
        }
    }

    void MakeStick(Vector3 from, Vector3 to, char el)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "Bond";
        if (Application.isPlaying) Destroy(go.GetComponent<Collider>()); else DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        Vector3 d = to - from;
        go.transform.localPosition = (from + to) * 0.5f;
        go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d);
        go.transform.localScale = new Vector3(bondRadius * 2f, d.magnitude * 0.5f, bondRadius * 2f);
        // Split colour: each half takes its atom's colour (hydrogen sticks stay white, others use atom colour).
        go.GetComponent<MeshRenderer>().sharedMaterial = MaterialFor(el);
    }

    static float Radius(char el)
    {
        switch (el) { case 'H': return 1.20f; case 'N': return 1.55f; case 'O': return 1.52f; default: return 1.70f; }
    }

    Material MaterialFor(char el)
    {
        switch (el) { case 'H': return hydrogen; case 'N': return nitrogen; case 'O': return oxygen; default: return carbon; }
    }
}
