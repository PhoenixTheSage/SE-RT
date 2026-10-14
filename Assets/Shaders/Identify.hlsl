#include "Common.hlsli"
RWTexture2D<uint2> OutIdentity:register(u0);
RWTexture2D<float4> OutGeometry:register(u1);
RWTexture2D<float4> OutPosition:register(u2);
// Restricted primary confirmation supplies stable instance/material guides without another lighting segment.
[numthreads(8,8,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID) {
    if(any(tid.xy>=Size))return;float4 p=OutPosition[tid.xy];OutIdentity[tid.xy]=0;OutGeometry[tid.xy]=0;
    if(p.w<=0 || length(p.xyz)>PrimaryRange)return;
    float distance=length(p.xyz);Hit h=tracePrimary(0,normalize(p.xyz),min(distance+.05,PrimaryRange));
    if(h.status!=1 || abs(h.distance-distance)>max(.05,distance*.001))return;
    float3 hp,n,c,e;float metal,rough;uint2 id;hitSurface(h,hp,n,c,metal,rough,e,id);OutIdentity[tid.xy]=id;float3 gp,gn;float error;hitGeometry(h,gp,gn,error);OutGeometry[tid.xy]=float4(gn,error);OutPosition[tid.xy]=float4(gp,p.w);
}
