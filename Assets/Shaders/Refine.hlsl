#include <GBuffer/GBuffer.hlsli>
#include "Transport.hlsli"
#include "Refinement.hlsli"
StructuredBuffer<uint> WorkCount:register(t60);
StructuredBuffer<uint2> WorkList:register(t61);
StructuredBuffer<RefinedSample> PreviousRefined:register(t64);
Texture2D<uint> PreviousLookup:register(t65);
RWStructuredBuffer<RefinedSample> Output:register(u0);
[numthreads(64,1,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID){uint index=tid.x;if(index>=min(WorkCount[0],RefinementCapacity))return;
    uint2 p=WorkList[index];RefinedSample result=(RefinedSample)0;result.pixel=p;SurfaceInterface surface=read_gbuffer(p);
    if(surface.native_depth==0){Output[index]=result;return;}
    float3 pos=surface.position-get_camera_position();float distance=length(pos);
    if(CollectStatistics!=0){uint unused;InterlockedAdd(Statistics[0],1,unused);}
    Hit h=tracePrimary(0,normalize(pos),min(distance+.05,PrimaryRange));
    if(h.status!=1||abs(h.distance-distance)>max(.05,distance*.001)){Output[index]=result;return;}
    float3 hp,hn,hc,emission;float hm,hr;uint2 identity;hitSurface(h,hp,hn,hc,hm,hr,emission,identity);
    float3 gp,gn;float error;hitGeometry(h,gp,gn,error);pos=gp;
    uint rng=hash(p.y*FullSize.x+p.x^Frame*7919^SamplingSeed);Reservoir reservoir=(Reservoir)0;
    [unroll]for(uint k=0;k<8;k++){float pdf;uint light=selectLight(rng,pdf);float2 uv=sampleContinuous(p,k*2);float t=target(light,uv,pos,surface.N,normalize(-pos),surface.base_color,surface.metalness,1-surface.gloss);add(reservoir,light,uv,t/pdf,t,1,rng);}
    LightingSample sample=transport(p,pos,surface.N,float4(surface.base_color,surface.metalness),1-surface.gloss,gn,error,reservoir,true);
    result.identity=identity;result.position=float4(pos,surface.depth);result.normal=float4(surface.N,1-surface.gloss);result.sun=sample.sun;result.metadata=sample.metadata;result.guide=sample.guide;result.key=sample.key;result.guideQuality=sample.guideQuality;
    [unroll]for(uint j=0;j<4;j++)result.radiance[j]=sample.radiance[j];
    int2 old=int2(floor(float2(p)+.5+Motion.Load(int3(p,0))));
    if(HistoryValid!=0&&all(old>=0)&&all(old<int2(FullSize))&&sample.metadata.x>0){uint2 tile=min(uint2(float2(old)*Size/FullSize),Size-1);uint encoded=PreviousLookup[tile];
        if(encoded>0&&encoded<=RefinementPixels){RefinedSample previous=PreviousRefined[encoded-1];
            if(all(previous.pixel==uint2(old))&&all(previous.identity==identity)&&previous.metadata.x>0&&abs(previous.position.w-surface.depth)<max(.02,surface.depth*.01)&&dot(previous.normal.xyz,surface.N)>.92){
                bool same=all(previous.key==sample.key),hardSelected=LampRadius==0&&max(previous.guide.w,sample.guide.w)<=1e-5;
                bool changed=hardSelected&&same&&abs(previous.metadata.z-sample.metadata.z)>.5;
                [unroll]for(uint j=0;j<4;j++)if(!changed||j>=2){float count=min(previous.radiance[j].a+1,sample.metadata.w<.5?2:4);result.radiance[j]=float4(lerp(previous.radiance[j].rgb,sample.radiance[j].rgb,1/count),count);}
                bool solarChanged=SolarRadius==0&&abs(luma(previous.sun.rgb)-luma(sample.sun.rgb))>.2;
                float count=solarChanged?1:min(previous.sun.a+1,sample.metadata.w<.5?2:4);result.sun=float4(lerp(previous.sun.rgb,sample.sun.rgb,1/count),count);
            }
        }
    }
    Output[index]=result;
}
