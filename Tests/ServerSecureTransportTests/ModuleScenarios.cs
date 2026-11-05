using System.Text;
using ServerTransport;

internal static class ModuleScenarios
{
    internal static void Run(Action<bool,string> check)
    {
        byte[] literal=[0,4,116,101,115,116,17,0,255];
        check(TlsModulePacket.Encode("test",new byte[]{17,0,255},9).SequenceEqual(literal),"Module encoding differs from independent literal profile");
        var parsed=TlsModulePacket.Decode(literal);
        check(parsed.Module=="test" && parsed.Payload.SequenceEqual(new byte[]{17,0,255}),"Module decoding differs from literal profile");
        foreach(byte[] invalid in new byte[][] {[],[0,0,1],[0,4,1],[0,129,1],[0,1,255]})
        {
            bool refused=false;try {TlsModulePacket.Decode(invalid);}catch(Exception e)when(e is InvalidDataException or DecoderFallbackException){refused=true;}
            check(refused,"Malformed module body was accepted");
        }
        foreach(var input in new[]{("",10),(new string('a',129),1024),("test",8),("test",-1)})
        {
            bool refused=false;try {TlsModulePacket.Encode(input.Item1,new byte[]{17,0,255},input.Item2);}catch(ArgumentOutOfRangeException){refused=true;}
            check(refused,"Invalid module/budget accepted");
        }
        byte[] payload=[1,2,3];byte[] owned=TlsModulePacket.Encode("x",payload,4096);payload[0]=99;
        check(TlsModulePacket.Decode(owned).Payload[0]==1,"Module encoding borrowed mutable payload bytes");
    }
}
