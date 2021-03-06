using Neo.SmartContract.Framework;

namespace Neo
{
    public class UInt160
    {
        public static extern UInt160 Zero { [OpCode(OpCode.PUSHDATA1, "140000000000000000000000000000000000000000")] get; }

        public extern bool IsZero
        {
            [OpCode(OpCode.PUSH0)]
            [OpCode(OpCode.NUMEQUAL)]
            get;
        }

        [OpCode(OpCode.CONVERT, StackItemType.ByteString)]
        [OpCode(OpCode.DUP)]
        [OpCode(OpCode.SIZE)]
        [OpCode(OpCode.PUSHINT8, "14")] // 0x14 == 20 bytes expected array size
        [OpCode(OpCode.NUMEQUAL)]
        [OpCode(OpCode.ASSERT)]
        public static extern explicit operator UInt160(byte[] value);

        [OpCode(OpCode.DUP)]
        [OpCode(OpCode.SIZE)]
        [OpCode(OpCode.PUSHINT8, "14")] // 0x14 == 20 bytes expected array size
        [OpCode(OpCode.NUMEQUAL)]
        [OpCode(OpCode.ASSERT)]
        public static extern explicit operator UInt160(ByteString value);

        [OpCode(OpCode.CONVERT, StackItemType.Buffer)]
        public static extern explicit operator byte[](UInt160 value);

        [Script]
        public static extern implicit operator ByteString(UInt160 value);
    }
}
