using System.Numerics;
using System.Runtime.InteropServices;

namespace SERT.Rendering;

[StructLayout(LayoutKind.Sequential,Pack=4)]
internal struct RefinementSample
{
    public uint PixelX,PixelY,Identity,Revision;
    public Vector4 Position,Normal,DirectDiffuse,DirectSpecular,IndirectDiffuse,IndirectSpecular,Sun,Metadata,Guide;
    public uint SourceIdentity,SourceRevision,SampleX,SampleY,GuideQuality,Padding0,Padding1,Padding2;
}
