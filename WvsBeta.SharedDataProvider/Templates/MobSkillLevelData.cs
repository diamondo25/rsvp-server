using System.Collections.Generic;
using System.Drawing;

namespace WvsBeta.SharedDataProvider.Templates
{
    public class MobSkillLevelData
    {
        public byte SkillID { get; set; }
        public byte Level { get; set; }
        public short Time { get; set; }
        public short MPConsume { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public byte Prop { get; set; }
        public short Cooldown { get; set; }

        public short LTX { get; set; }
        public short LTY { get; set; }
        public short RBX { get; set; }
        public short RBY { get; set; }

        public Rectangle GetAffectedArea(bool facingLeft) => Rectangle.FromLTRB(facingLeft ? -LTX : LTX, LTY, facingLeft ? -RBX : RBX, RBY);

        public byte HPLimit { get; set; }
        public ushort SummonLimit { get; set; }
        public byte SummonEffect { get; set; }
        public List<int> Summons { get; set; }
    }
}