#include <Lighting/Light.hlsli>
#include <Postprocess/PostprocessBase.hlsli>
cbuffer Parameters:register(b1){uint2 FullSize,Size;uint4 Counts;uint4 Counts2;float4 SunDirection,SunColor,Sky;row_major float4x4 PreviousViewProjection;float4 CameraDelta;uint4 History;float Exposure,PrimaryRange,DiffuseRange,SpecularRange;uint CollectStatistics,ShadowFlags;float ShadowBias;uint ShadowDebugView;float LampRadius,SolarRadius,GlassTransmission,RefinementFraction;uint BiasMode,SamplingMode,SamplingSeed,FarSunValid;row_major float4x4 FarSunMatrix;uint RefinementCapacity,RefinementPixels,QualityPadding0,QualityPadding1;};
Texture2D<float4> PositionDepth:register(t24);Texture2D<float4> NormalRoughness:register(t25);
Texture2D<uint2> Identity:register(t27);Texture2DArray<float4> Radiance:register(t29);Texture2D<float4> Metadata:register(t31);Texture2D<float4> SunVisibility:register(t34);
Texture2D<float4> ShadowGuide:register(t53);
Texture2D<uint> GuideQuality:register(t66);
Texture2D<float> FarSunDepth:register(t59);
#include "Refinement.hlsli"
StructuredBuffer<RefinedSample> Refined:register(t62);
Texture2D<uint> Lookup:register(t63);
float3 __pixel_shader(float4 screen:SV_Position):SV_Target0 {
    uint2 p=uint2(screen.xy);SurfaceInterface s=read_gbuffer(p);if(s.native_depth==0)discard;
    if(s.depth>PrimaryRange){if(ShadowDebugView==3)return float3(1,0,1);discard;}
    float2 low=screen.xy*Size/FullSize-.5;int2 base=int2(floor(low));float3 dd=0,ds=0,id=0,is=0;float3 sun=0;float sum=0;
    [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++){
        int2 q=base+int2(x,y);if(any(q<0)||any(q>=int2(Size))||Metadata[q].x<=0||Identity[q].x==0)continue;
        float4 guide=PositionDepth[q];float3 normal=NormalRoughness[q].xyz;
        float depthWeight=exp(-abs(guide.w-s.depth)/max(.02,s.depth*.005)),normalWeight=pow(max(dot(normal,s.N),0),32);
        if(depthWeight<.1||normalWeight<.5)continue;
        float2 frac=abs(low-q);float w=(1-frac.x)*(1-frac.y)*depthWeight*normalWeight;
        dd+=Radiance.Load(int4(q,0,0)).rgb*w;ds+=Radiance.Load(int4(q,1,0)).rgb*w;id+=Radiance.Load(int4(q,2,0)).rgb*w;is+=Radiance.Load(int4(q,3,0)).rgb*w;sun+=SunVisibility[q].rgb*w;sum+=w;
    }
    uint2 tile=min(uint2(screen.xy*Size/FullSize),Size-1);uint encoded=Lookup[tile];bool refined=false;
    uint debugQuality=GuideQuality[tile];float4 debugMetadata=Metadata[tile];float historyAge=Radiance.Load(int4(tile,0,0)).a;
    if(encoded>0&&encoded<=RefinementCapacity){RefinedSample r=Refined[encoded-1];if(all(r.pixel==p)&&r.metadata.x>0){dd=r.radiance[0].rgb;ds=r.radiance[1].rgb;id=r.radiance[2].rgb;is=r.radiance[3].rgb;sun=r.sun.rgb;sum=1;refined=true;debugQuality=r.guideQuality;debugMetadata=r.metadata;historyAge=r.radiance[0].a;}}
    if(ShadowDebugView==6)return refined?float3(1,1,0):float3(0,.15,0);
    if(ShadowDebugView==8){if(FarSunValid==0)return float3(1,.25,0);float4 clip=mul(float4(s.position-get_camera_position(),1),FarSunMatrix);float3 uvz=float3(clip.x/clip.w*.5+.5,.5-clip.y/clip.w*.5,clip.z/clip.w);if(any(uvz<0)||any(uvz>1))return float3(1,0,1);return uvz.z-5e-6<=FarSunDepth.Load(int3(min(uint2(uvz.xy*2048),2047),0))?float3(0,1,0):float3(0,0,1);}
    if(ShadowDebugView==3)return sum>=.1?float3(0,1,0):float3(1,0,1);
    if(sum<.1)discard;
    if(ShadowDebugView==1){float visibility=dot(sun/sum,float3(.2126,.7152,.0722));return visibility.xxx;}
    if(ShadowDebugView==7)return sun/sum;
    if(ShadowDebugView==4){uint solar=debugQuality&3,direct=(debugQuality>>2)&3;uint weakest=solar==0?direct:direct==0?solar:min(solar,direct);return weakest==2?float3(0,1,0):weakest==1?float3(1,.5,0):float3(0,.25,1);}
    if(ShadowDebugView==5)return float3(debugMetadata.w,saturate(historyAge/32),0);
    if(ShadowDebugView==2){int2 q=clamp(int2(round(low)),0,int2(Size)-1);float visibility=refined?debugMetadata.z:Metadata[q].x>0?Metadata[q].z:-1;return visibility<0?float3(0,0,1):visibility.xxx;}
    float3 color=s.base_color*(dd+id)/(sum*Exposure)+(ds+is)/(sum*Exposure);
    color+=MainDirectionalLight(s)*(sun/sum);
    if(1-s.gloss<.2)color+=AmbientSpecular(s.f0,s.gloss,s.N,s.V,GetAmbientFarFactor(s.depth))*frame_.Light.ambientSpecularFactor;
    color+=s.albedo*s.emissive*frame_.Post.BloomEmissiveness;
    return Fog(max(color,0),s.depth);
}
