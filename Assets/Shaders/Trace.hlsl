#include "Transport.hlsli"
RWTexture2DArray<float4> OutRadiance:register(u0);
RWTexture2D<float4> OutSun:register(u1);
RWTexture2D<float4> OutMetadata:register(u2);
RWTexture2D<uint4> OutKey:register(u4);
RWTexture2D<float4> OutGuide:register(u5);
RWTexture2D<uint> OutQuality:register(u6);
[numthreads(8,8,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID){uint2 p=tid.xy;if(any(p>=Size))return;
    [unroll]for(uint i=0;i<4;i++)OutRadiance[uint3(p,i)]=0;OutSun[p]=float4(1,1,1,0);OutMetadata[p]=0;OutKey[p]=0;OutGuide[p]=0;OutQuality[p]=0;
    if(Identity[p].x==0)return;uint2 full=min(uint2((float2(p)+.5)*FullSize/Size),FullSize-1);
    float4 geometry=GeometryGuide[p];float3 gn=length(geometry.xyz)>.5?geometry.xyz:NormalRoughness[p].xyz;
    LightingSample sample=transport(full,PositionDepth[p].xyz,NormalRoughness[p].xyz,AlbedoMetal[p],NormalRoughness[p].w,gn,max(geometry.w,rayError(PositionDepth[p].xyz)),Reservoirs[p.y*Size.x+p.x],false);
    [unroll]for(uint i=0;i<4;i++)OutRadiance[uint3(p,i)]=sample.radiance[i];OutSun[p]=sample.sun;OutMetadata[p]=sample.metadata;OutKey[p]=sample.key;OutGuide[p]=sample.guide;OutQuality[p]=sample.guideQuality;
}
