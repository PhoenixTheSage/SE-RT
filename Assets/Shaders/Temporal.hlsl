#include "Common.hlsli"
RWTexture2DArray<float4> OutRadiance:register(u0);
RWTexture2D<float4> OutMoments:register(u1);
RWTexture2D<float4> OutSun:register(u2);
RWTexture2D<float4> OutFast:register(u4);
RWTexture2D<float4> OutSunFast:register(u5);
Texture2D<float4> PreviousSun:register(t45);
[numthreads(8,8,1)]void __compute_shader(uint3 tid:SV_DispatchThreadID){uint2 p=tid.xy;if(any(p>=Size))return;
    int2 old=previousPixel(p);bool accept=HistoryValid!=0&&all(old>=0)&&compatible(p,uint2(old))&&Metadata[p].x>0;
    float4 variance=0,fast=0;float4 solar=SunVisibility[p];float4 previousFast=accept?PreviousSunFast[old]:solar;
    float fastCount=accept?min(previousFast.a+1,4):1;
    float3 fastSun=lerp(previousFast.rgb,solar.rgb,1/fastCount);OutSunFast[p]=float4(fastSun,fastCount);
    float sunVariance=max(.02,luma(previousFast.rgb)*(1-luma(previousFast.rgb)));
    bool sunReactive=accept&&max(abs(solar.r-previousFast.r),max(abs(solar.g-previousFast.g),abs(solar.b-previousFast.b)))>max(.2,2*sqrt(sunVariance));
    if(accept){float3 stable=PreviousSun[old].rgb;float delta=max(abs(fastSun.r-stable.r),max(abs(fastSun.g-stable.g),abs(fastSun.b-stable.b)));
        float threshold=max(.15,.75*sqrt(sunVariance*(1/fastCount+1/max(PreviousSun[old].a,1))));sunReactive=sunReactive||delta>threshold;}
    float sunCap=Metadata[p].w<.5||sunReactive?2:32;
    float sunCount=accept?min(PreviousSun[old].a+1,sunCap):1;
    float3 sun=accept?lerp(clamp(PreviousSun[old].rgb,0,1),solar.rgb,1/sunCount):solar.rgb;
    if(SolarRadius==0&&accept&&abs(luma(solar.rgb)-luma(PreviousSun[old].rgb))>.2){sun=solar.rgb;sunCount=1;}
    OutSun[p]=float4(sun,solar.a>0?sunCount:0);
    [unroll]for(uint layer=0;layer<4;layer++){
        float4 current=Radiance.Load(int4(p,layer,0));float3 result=current.rgb;float count=1,second=luma(result)*luma(result);
        int2 sampleOld=layer%2==1?previousSpecularPixel(p,old):old;
        bool layerAccept=accept&&all(sampleOld>=0)&&compatible(p,uint2(sampleOld));
        bool hardSelected=LampRadius==0&&max(ShadowGuide[p].w,PreviousShadowGuide[sampleOld].w)<=1e-5;
        if(layer<2&&layerAccept&&hardSelected&&all(SourceKey[p]==PreviousSourceKey[sampleOld])&&abs(Metadata[p].z-PreviousMetadata[sampleOld].z)>.5)layerAccept=false;
        if(layer%2==1&&layerAccept)layerAccept=abs(Metadata[p].y-PreviousMetadata[sampleOld].y)<max(.1,Metadata[p].y*.2);
        float previousMean=layerAccept?PreviousFastHistory[sampleOld][layer]:luma(result);
        float fastAlpha=layerAccept?.25:1;fast[layer]=lerp(previousMean,luma(result),fastAlpha);
        if(layerAccept&&current.a>0){float4 history=PreviousRadiance.Load(int4(sampleOld,layer,0));float3 lo=1e30,hi=0;float maximum=0;
            [unroll]for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++){int2 q=clamp(int2(p)+int2(x,y),0,int2(Size)-1);float4 v=Radiance.Load(int4(q,layer,0));if(v.a>0){lo=min(lo,v.rgb);hi=max(hi,v.rgb);maximum=max(maximum,luma(v.rgb));}}
            if(maximum>0)history.rgb=clamp(history.rgb,max(0,lo*.5),max(hi*2,current.rgb));
            float previousVariance=max(Moments[sampleOld][layer]-previousMean*previousMean,0);
            bool reactive=abs(luma(result)-previousMean)>max(.02,abs(previousMean)*.5+2*sqrt(previousVariance));
            reactive=reactive||abs(fast[layer]-luma(history.rgb))>max(.02,abs(previousMean)*.15+.75*sqrt(previousVariance*(.25+1/max(history.a,1))));
            float cap=Metadata[p].w<.5||reactive?2:32;count=min(history.a+1,cap);
            float alpha=1/count;result=lerp(current.rgb,history.rgb,1-alpha);
            second=lerp(second,Moments[sampleOld][layer],1-alpha);
        }
        if(current.a<=0){result=0;count=0;second=0;fast[layer]=0;}
        variance[layer]=second;OutRadiance[uint3(p,layer)]=float4(result,count);
    }OutMoments[p]=variance;OutFast[p]=fast;
}
