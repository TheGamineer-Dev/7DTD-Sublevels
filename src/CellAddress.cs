using System;

namespace SubLevelPhysics
{
    /// <summary>
    /// Logical coordinates are relative to a sublevel. Physical coordinates identify a chunk
    /// in the dedicated storage world. They are deliberately unrelated: adjacent logical cells
    /// do not need to be adjacent physical chunks.
    /// </summary>
    public struct CellAddress : IEquatable<CellAddress>
    {
        public readonly int X;
        public readonly int Z;
        public CellAddress(int x, int z) { X = x; Z = z; }
        public bool Equals(CellAddress other) { return X == other.X && Z == other.Z; }
        public override bool Equals(object obj) { return obj is CellAddress && Equals((CellAddress)obj); }
        public override int GetHashCode() { unchecked { return (X * 397) ^ Z; } }
        public override string ToString() { return X + "," + Z; }
    }

    public struct PhysicalCell : IEquatable<PhysicalCell>
    {
        public readonly int X;
        public readonly int Z;
        public PhysicalCell(int x, int z) { X = x; Z = z; }
        public bool Equals(PhysicalCell other) { return X == other.X && Z == other.Z; }
        public override bool Equals(object obj) { return obj is PhysicalCell && Equals((PhysicalCell)obj); }
        public override int GetHashCode() { unchecked { return (X * 397) ^ Z; } }
        public override string ToString() { return X + "," + Z; }
    }

    public sealed class CellRecord
    {
        public string SublevelId { get; internal set; }
        public CellAddress Logical { get; internal set; }
        public PhysicalCell Physical { get; internal set; }
        public long LastTouchedUtcTicks { get; internal set; }
        public bool Retiring { get; internal set; }
    }
}
