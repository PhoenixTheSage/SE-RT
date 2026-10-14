#include <GBuffer/GBuffer.hlsli>
#include "Common.hlsli"
RWStructuredBuffer<uint> WorkCount:register(u0);
RWStructuredBuffer<uint2> WorkList:register(u1);
RWTexture2D<uint> Lookup:register(u2);
[numthreads(8,8,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID){uint2 p=tid.xy;if(any(p>=Size)||RefinementCapacity==0||Lookup[p]!=0)return;
    uint2 begin=uint2(ceil(float2(p)*FullSize/Size)),end=min(uint2(ceil(float2(p+1)*FullSize/Size)),FullSize);
    float score=-1;uint2 chosen=begin;float4 guide=PositionDepth[p];bool disoccluded=HistoryValid==0;
    int2 previous=previousPixel(p);if(any(previous<0)||!compatible(p,uint2(previous)))disoccluded=true;
    float edge=0;[unroll]for(int i=0;i<4;i++){int2 q=clamp(int2(p)+int2(i==0?-1:i==1?1:0,i==2?-1:i==3?1:0),0,int2(Size)-1);edge=max(edge,abs(luma(SunVisibility[p].rgb)-luma(SunVisibility[q].rgb)));if(all(SourceKey[p]==SourceKey[q]))edge=max(edge,abs(Metadata[p].z-Metadata[q].z));}
    [loop]for(uint y=begin.y;y<end.y;y++)[loop]for(uint x=begin.x;x<end.x;x++){
        SurfaceInterface s=read_gbuffer(uint2(x,y));if(s.native_depth==0||s.depth>PrimaryRange)continue;
        float value=0;if(disoccluded||Metadata[p].x<=0)value+=8;if(Metadata[p].w<.5)value+=4;
        if(abs(guide.w-s.depth)>max(.02,s.depth*.01)||dot(NormalRoughness[p].xyz,s.N)<.92)value+=12;
        if(edge>.25)value+=3;
        if(value>score){score=value;chosen=uint2(x,y);}
    }
    if(score<3)return;if(QualityPadding0==0&&score<12)return;if(QualityPadding0==1&&score>=12)return;
    // Independent scheduling stream spreads capped work rather than favoring fixed scanline blocks.
    if(score<12&&sampleContinuous(p,48).x>max(.1,RefinementFraction*8))return;
    uint index;InterlockedAdd(WorkCount[0],1,index);if(index>=RefinementCapacity)return;
    WorkList[index]=chosen;Lookup[p]=index+1;
}
