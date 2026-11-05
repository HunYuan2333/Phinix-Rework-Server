using Google.Protobuf;
using Trading;
using Utils.Framework;

internal static class LegacyItemTranslation
{
    internal static FrameworkItemPayload Translate(ProtoThing original)
    {
        ProtoThing Known(ProtoThing thing,int depth)
        {
            if(depth>32||string.IsNullOrWhiteSpace(thing.DefName)||thing.StackCount<=0||!Enum.IsDefined(thing.Quality)||thing.DefName=="__PHINIX_STATEFUL_ITEM_V1__")
                throw new InvalidDataException("Unsupported legacy item cannot be translated losslessly; quarantine the complete source.");
            return new ProtoThing {DefName=thing.DefName,StackCount=thing.StackCount,StuffDefName=thing.StuffDefName,Quality=thing.Quality,HitPoints=thing.HitPoints,
                InnerProtoThing=thing.InnerProtoThing==null?null:Known(thing.InnerProtoThing,depth+1)};
        }
        var known=Known(original,0);
        if(!known.ToByteArray().SequenceEqual(original.ToByteArray()))throw new InvalidDataException("Unknown protobuf fields require an explicit adapter; no field loss allowed.");
        global::Phinix.Framework.FrameworkVanillaItemData Convert(ProtoThing t)=>new()
        {DefName=t.DefName,StackCount=t.StackCount,StuffDefName=t.StuffDefName,HitPoints=t.HitPoints,Quality=(global::Phinix.Framework.FrameworkItemQuality)((int)t.Quality+1),InnerItem=t.InnerProtoThing==null?null:Convert(t.InnerProtoThing)};
        return new FrameworkItemPayload {CodecId="core.item.vanilla",PayloadBytes=FrameworkSerialization.SerializeItemData(Convert(known))};
    }
}
