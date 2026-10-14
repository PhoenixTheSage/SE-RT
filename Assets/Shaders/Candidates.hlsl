#include "Common.hlsli"
RWStructuredBuffer<Reservoir> OutReservoir:register(u0);
[numthreads(8,8,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID) {
    uint2 p=tid.xy;if(any(p>=Size))return;uint index=p.y*Size.x+p.x,rng=hash(index^Frame*7919);Reservoir r=(Reservoir)0;
    if(Identity[p].x==0){OutReservoir[index]=r;return;}
    float3 pos=PositionDepth[p].xyz,n=NormalRoughness[p].xyz,v=normalize(-pos);float4 color=AlbedoMetal[p];float rough=NormalRoughness[p].w;
    [unroll]for(uint k=0;k<8;k++){float pdf;uint light=selectLight(rng,pdf);float2 uv=sampleContinuous(p,uint(k)*2);float t=target(light,uv,pos,n,v,color.rgb,color.a,rough);add(r,light,uv,t/pdf,t,1,rng);}
    OutReservoir[index]=r;
}
