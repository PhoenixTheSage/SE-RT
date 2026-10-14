#include "Common.hlsli"
StructuredBuffer<Reservoir> PreviousReservoir:register(t39);
RWStructuredBuffer<Reservoir> OutReservoir:register(u0);
[numthreads(8,8,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID) {
    uint2 p=tid.xy;if(any(p>=Size))return;uint index=p.y*Size.x+p.x,rng=hash(index^Frame*6211);Reservoir r=Reservoirs[index];
    if(Identity[p].x==0){OutReservoir[index]=(Reservoir)0;return;}
    float3 pos=PositionDepth[p].xyz,n=NormalRoughness[p].xyz,v=normalize(-pos);float4 color=AlbedoMetal[p];float rough=NormalRoughness[p].w;
    int2 old=previousPixel(p);
    [unroll]for(int k=-1;k<4;k++) {
        Reservoir s=(Reservoir)0;
        if(k<0){if(HistoryValid==0 || any(old<0) || !compatible(p,uint2(old)))continue;s=PreviousReservoir[old.y*Size.x+old.x];}
        else {int2 q=int2(p)+int2(cos((k+random(rng))*PI*.5),sin((k+random(rng))*PI*.5))*int(2+random(rng)*8);
            if(any(q<0)||any(q>=int2(Size))||any(Identity[q]!=Identity[p])||dot(NormalRoughness[q].xyz,n)<.92||abs(PositionDepth[q].w-PositionDepth[p].w)>max(.02,PositionDepth[p].w*.01))continue;
            s=Reservoirs[q.y*Size.x+q.x];}
        if(s.count==0||s.target<=0||s.light>=LightCount||s.revision!=hash(Lights[s.light].revision^Lights[s.light].identity))continue;
        uint count=min(s.count,32u);float t=target(s.light,s.uv,pos,n,v,color.rgb,color.a,rough);float weight=t*s.sum/(s.count*s.target)*count;
        add(r,s.light,s.uv,weight,t,count,rng);
    }
    OutReservoir[index]=r;
}
