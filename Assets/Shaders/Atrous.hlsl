#include "Common.hlsli"
RWTexture2DArray<float4> OutRadiance:register(u0);
RWTexture2D<float4> OutSun:register(u1);
[numthreads(8,8,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID){uint2 p=tid.xy;if(any(p>=Size))return;
    float4 center=PositionDepth[p],normal=NormalRoughness[p];uint2 id=Identity[p];uint step=1u<<Iteration;
    float3 sunSum=0;float sunWeight=0;
    [unroll]for(uint layer=0;layer<4;layer++){
        float4 c=Radiance.Load(int4(p,layer,0));float3 sum=0;float weights=0;float variance=max(Moments[p][layer]-pow(luma(c.rgb),2),1e-5);
        [unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++){
            int2 q=int2(p)+int2(x,y)*int(step);if(any(q<0)||any(q>=int2(Size))||any(Identity[q]!=id))continue;
            float4 v=Radiance.Load(int4(q,layer,0));if(v.a<=0)continue;
            float geometry=pow(max(0,dot(normal.xyz,NormalRoughness[q].xyz)),32)*exp(-abs(center.w-PositionDepth[q].w)/max(.02,center.w*.005));
            if(layer%2==1)geometry*=exp(-abs(Metadata[p].y-Metadata[q].y)/max(.1,Metadata[p].y*.1));
            float penumbra=max(ShadowGuide[p].w,ShadowGuide[q].w)*Size.y/max(center.w,1);
            bool sameSource=SourceKey[p].x!=0&&all(SourceKey[p]==SourceKey[q]);float contact=penumbra<.5&&sameSource?exp(-abs(Metadata[p].z-Metadata[q].z)*8):1;
            float confidence=min(Metadata[p].w,Metadata[q].w);
            float w=geometry*contact*max(confidence,.1)*(x==0?2:1)*(y==0?2:1)*exp(-abs(luma(c.rgb)-luma(v.rgb))/max(.01,4*sqrt(variance)));
            sum+=v.rgb*w;weights+=w;if(layer==0){uint quality=GuideQuality[p]&3,otherQuality=GuideQuality[q]&3;float penumbra=quality>0&&otherQuality>0?max(ShadowGuide[p].z,ShadowGuide[q].z):quality>0?ShadowGuide[p].z:otherQuality>0?ShadowGuide[q].z:0;float radius=penumbra*Size.y/max(center.w,1);float shadowEdge=radius<.5?exp(-abs(luma(SunVisibility[p].rgb)-luma(SunVisibility[q].rgb))*64):quality>0&&otherQuality>0?exp(-abs(ShadowGuide[p].x-ShadowGuide[q].x)/max(.1,ShadowGuide[p].x*.2)):1;sunSum+=SunVisibility[q].rgb*geometry*shadowEdge;sunWeight+=geometry*shadowEdge;}
        }
        OutRadiance[uint3(p,layer)]=float4(weights>0?sum/weights:c.rgb,c.a);
    }OutSun[p]=float4(sunWeight>0?sunSum/sunWeight:SunVisibility[p].rgb,SunVisibility[p].a);
}
