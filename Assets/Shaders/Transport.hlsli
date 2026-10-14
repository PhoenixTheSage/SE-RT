#ifndef SERT_TRANSPORT
#define SERT_TRANSPORT
#include "Shadows.hlsli"
struct LightingSample{float4 radiance[4];float4 sun,metadata,guide;uint4 key;uint guideQuality;};
float mixturePdf(float3 n,float3 v,float3 d,float rough) {
    float nl=max(dot(n,d),0);float3 h=normalize(v+d);float nh=max(dot(n,h),0),a=max(.02,rough*rough),den=nh*nh*(a*a-1)+1;
    float s=a*a/(PI*den*den)*nh/max(4*abs(dot(v,h)),1e-5);
    return rough<.2?nl/PI:.5*(nl/PI+s);
}
float lightPdf(uint index,float2 uv,float3 pos,float3 n,float3 v,float3 color,float metal,float rough) {
    float3 d,s,l;float distance,pdf;evaluateLight(index,uv,pos,n,v,color,metal,rough,d,s,l,distance,pdf);
    float total=LightTree[1];return pdf*(.05/LightCount+(total>0?.95*LightTree[TreeLeaves+index]/total:.95/LightCount));
}
float power(float a,float b){return a*a/max(a*a+b*b,1e-20);}
LightingSample transport(uint2 fullPixel,float3 pos,float3 n,float4 color,float rough,float3 gn,float error,Reservoir r,bool nearest){
    LightingSample result=(LightingSample)0;uint rng=hash(fullPixel.y*FullSize.x+fullPixel.x^Frame*3571^SamplingSeed);float3 v=normalize(-pos);
    bool allValid=true;ShadowResult solar=sunShadow(pos,gn,error,sampleContinuous(fullPixel,24),nearest);allValid=solar.status<2;
    float3 solarT=solar.transmission;if(FarSunValid==0||(ShadowFlags&1)!=0)solarT=min(solarT,NativeSunShadows[fullPixel].xxx);
    result.sun=float4(solarT,1);result.guide.x=solar.blocker;result.guide.z=solar.blocker*SolarRadius;result.guideQuality=solar.quality|(solar.status<<4);
    float3 dd=0,ds=0,id=0,is=0;
    float selectedVisibility=-1;float confidence=1;
    if(r.count>0 && r.target>0 && r.light<LightCount) {
        float3 ld,ls,d;float distance,pdf;evaluateLight(r.light,r.uv,pos,n,v,color.rgb,color.a,rough,ld,ls,d,distance,pdf);
        ShadowResult direct=(ShadowResult)0;
        if(luma(ld*color.rgb+ls)>0){direct=lampShadow(Lights[r.light],r.uv,pos,gn,error,d,distance,nearest);selectedVisibility=luma(direct.transmission);}
        allValid=allValid&&direct.status<2;result.key=shadowKey(Lights[r.light],r.uv);
        result.guideQuality|=(direct.quality<<2)|(direct.status<<6);
        result.guide.y=direct.blocker;float radius=Lights[r.light].kind<2?min(LampRadius,distance*.25):0;
        if(Lights[r.light].kind==2){Triangle t=Triangles[Lights[r.light].triIndex];Instance inst=Instances[Lights[r.light].instance];float3 a=mul(float4(Vertices[t.a].p,1),inst.world).xyz,b=mul(float4(Vertices[t.b].p,1),inst.world).xyz,c=mul(float4(Vertices[t.c].p,1),inst.world).xyz;radius=sqrt(length(cross(b-a,c-a))*.5/PI);}
        if(Lights[r.light].kind==4)radius=Lights[r.light].range;
        result.guide.w=radius*direct.blocker/max(distance-direct.blocker,max(error,1e-4));
        confidence=(direct.quality==1||solar.quality==1)?.65:1;
        float weight=r.sum/(r.count*r.target);
        if(Lights[r.light].kind==2 || Lights[r.light].kind==3)weight*=power(lightPdf(r.light,r.uv,pos,n,v,color.rgb,color.a,rough),mixturePdf(n,v,d,rough));
        if(Lights[r.light].kind==3 && rough<.2)ls=0;
        dd=ld*weight*direct.transmission;ds=ls*weight*direct.transmission;
    }
    bool spec=rough>=.2 && random(rng)<.5;float sampledPdf;float3 direction=sampleLobe(n,v,rough,spec,sampleContinuous(fullPixel,32),sampledPdf);
    float pdf=mixturePdf(n,v,direction,rough),hitDistance=0;
    if(pdf>1e-6 && dot(n,direction)>0) {
        if(CollectStatistics!=0){uint unused;InterlockedAdd(Statistics[0],1,unused);}Hit hit=trace(surfaceOffset(pos,gn,direction,error),direction,spec?SpecularRange:DiffuseRange);
        allValid=allValid&&hit.status<2;float3 incoming=0;
        if(hit.status==0) {
            // This is the BSDF counterpart of primary environment NEE, not an extra bounce.
            float3 env=Environment.SampleLevel(LinearClamp,float3(-direction.x,direction.y,direction.z),0).rgb;
            float total=LightTree[1];float environmentChoice=.05/LightCount+(total>0?.95*LightTree[TreeLeaves+LightCount-1]/total:.95/LightCount);
            float environmentPdf=environmentChoice*max(dot(n,direction),0)/PI;
            incoming=env*power(pdf,environmentPdf);
        } else if(hit.status==1) {
            Material hitMaterial=Materials[Instances[hit.instance].materialBase+Triangles[hit.triIndex].material];
            allValid=allValid&&(hitMaterial.flags&8)==0;
            float3 hp,hn,hc,emission;float hm,hr;uint2 identity;hitSurface(hit,hp,hn,hc,hm,hr,emission,identity);if(dot(hn,-direction)<0)hn=-hn;
            hitDistance=hit.distance;float3 gp,secondaryGn;float secondaryError;hitGeometry(hit,gp,secondaryGn,secondaryError);
            // Evaluate the competing area-light PDF at the actual continuation hit.
            uint emissiveIndex=0xffffffff;
            // Native prefix, ordered mesh emitters, fixed proxy slots, then environment.
            if(luma(emission)>0){uint lo=uint(Sky.w),hi=LightCount>=513?LightCount-513:lo;
                [loop]while(lo<hi){uint k=lo+(hi-lo)/2;Light l=Lights[k];
                    if(l.instance<hit.instance || (l.instance==hit.instance && l.triIndex<hit.triIndex))lo=k+1;else hi=k;}
                if(lo<LightCount && Lights[lo].kind==2 && Lights[lo].instance==hit.instance && Lights[lo].triIndex==hit.triIndex)emissiveIndex=lo;}
            float emitterPdf=0;
            if(emissiveIndex!=0xffffffff){Triangle t=Triangles[hit.triIndex];Instance inst=Instances[hit.instance];
                float3 a=mul(float4(Vertices[t.a].p,1),inst.world).xyz,b=mul(float4(Vertices[t.b].p,1),inst.world).xyz,c=mul(float4(Vertices[t.c].p,1),inst.world).xyz;
                float area=length(cross(b-a,c-a))*.5,total=LightTree[1];float q=.05/LightCount+(total>0?.95*LightTree[TreeLeaves+emissiveIndex]/total:.95/LightCount);
                float3 delta=hp-pos;float3 geometricNormal=normalize(cross(b-a,c-a));
                emitterPdf=q*dot(delta,delta)/max(area*abs(dot(geometricNormal,-normalize(delta))),1e-8);}
            incoming=emission*power(pdf,emitterPdf);
            // One secondary-hit NEE segment; sample sun versus the complete finite/environment catalog.
            float3 secondaryDiffuse,secondarySpecular;ShadowResult secondary=(ShadowResult)0;
            if(random(rng)<.5){float3 bd,bs;brdf(hn,-direction,normalize(SunDirection.xyz),hc,hm,hr,bd,bs);secondaryDiffuse=SunColor.rgb*(hc*bd+bs)*2;
                if(luma(secondaryDiffuse)>0)secondary=sunShadow(hp,secondaryGn,secondaryError,sampleContinuous(fullPixel,40),nearest);}
            else {float proposal;uint light=selectLight(rng,proposal);float3 d;float distance,conditional;float2 uv=sampleContinuous(fullPixel,42);
                evaluateLight(light,uv,hp,hn,-direction,hc,hm,hr,secondaryDiffuse,secondarySpecular,d,distance,conditional);secondaryDiffuse=(hc*secondaryDiffuse+secondarySpecular)*2/proposal;
                if(luma(secondaryDiffuse)>0)secondary=lampShadow(Lights[light],uv,hp,secondaryGn,secondaryError,d,distance,nearest);}
            allValid=allValid&&secondary.status<2;incoming+=secondaryDiffuse*secondary.transmission;
        }
        float3 bd,bs;brdf(n,v,direction,color.rgb,color.a,rough,bd,bs);
        if(hit.status==1){id=incoming*bd/pdf;if(rough>=.2)is=incoming*bs/pdf;}
        else if(hit.status==0){dd+=incoming*bd/pdf;if(rough>=.2)ds+=incoming*bs/pdf;}
    }
    if(!allValid){if(CollectStatistics!=0){uint unused;InterlockedAdd(Statistics[1],1,unused);}return result;}
    float3 signals[4]={dd,ds,id,is};[unroll]for(uint layer=0;layer<4;layer++)result.radiance[layer]=float4(min(safe(signals[layer])*Exposure,60000),1);
    result.metadata=float4(1,hitDistance,selectedVisibility,confidence);return result;

}
#endif
