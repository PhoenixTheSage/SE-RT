#ifndef SERT_SHADOWS
#define SERT_SHADOWS
#include "Common.hlsli"
RWStructuredBuffer<uint> Statistics:register(u3);
struct ShadowResult { float3 transmission; uint status; float blocker; uint quality; };
uint4 shadowKey(Light l,float2 uv){bool sampled=l.kind>=2||LampRadius>0;return uint4(l.identity,l.revision^hash(l.kind),sampled?asuint(uv.x):0,sampled?asuint(uv.y):0);}
// Kept for standalone compatibility probes; production history uses the full integer key.
uint shadowSampleToken(Light l,float2 uv){uint4 key=shadowKey(l,uv);return hash(key.x^key.y^key.z^key.w)&0x3fffff;}
float sphereExit(float3 o,float3 d){float b=dot(o,d),c=dot(o,o)-256*256;return c>0?0:max(0,-b+sqrt(max(0,b*b-c)));}
ShadowResult failure(uint status){ShadowResult r=(ShadowResult)0;r.status=status;return r;}
float3 thinTransmission(Material m,Triangle t,float2 bary){
    float2 uv=textureUv(t,bary)*m.opticalUv.zw+m.opticalUv.xy;
    float4 texel=ColorMetal.SampleLevel(LinearWrap,float3(uv,m.atlasIndex),0);
    float3 tint=max(m.optical.rgb*m.color,0);tint=saturate(tint/max(max(tint.x,max(tint.y,tint.z)),1e-6))*saturate(texel.rgb);
    float opacity=saturate(m.optical.w*texel.a);return lerp(float3(1,1,1),GlassTransmission*tint,opacity);
}
#ifdef SERT_RAY_QUERY
// Ray-query visibility with the software rules. An unknown candidate counts when it is nearer than the final
// limit (the blocker once one is known), which is the order-independent form of the software test.
ShadowResult traceVisibility(float3 o,float3 d,float maximum,bool nearest){
    if(InstanceCount==0||dot(o,o)>256*256)return failure(2);
    float horizon=sphereExit(o,d);bool unknown=maximum>horizon;maximum=min(maximum,horizon);
    float distances[8];float3 normals[8],transmissions[8];uint layers=0;
    bool opaque=false;float blocker=maximum,unknownAt=maximum+1;
    RayDesc ray;ray.Origin=o;ray.Direction=d;ray.TMin=queryMinimum(o,d);ray.TMax=maximum;
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> q;q.TraceRayInline(SceneTlas,0,0xFF,ray);
    [loop]while(q.Proceed()){
        uint k=q.CandidateInstanceID();if(k>=InstanceCount){unknown=true;continue;}Instance inst=Instances[k];
        if(q.CandidateType()!=CANDIDATE_NON_OPAQUE_TRIANGLE){float nt;if(box(o,d,inst.lo,inst.hi,maximum,nt))unknownAt=min(unknownAt,nt);continue;}
        uint ti=InstanceTriangleBase[k]+q.CandidatePrimitiveIndex();if(ti>=TriangleCount){unknown=true;continue;}
        Triangle tri=Triangles[ti];float distance=q.CandidateTriangleRayT();float2 bary=q.CandidateTriangleBarycentrics();
        uint mi=inst.materialBase+tri.material;if(mi>=MaterialCount){unknownAt=min(unknownAt,distance);continue;}Material mat=Materials[mi];
        if((mat.flags&8)!=0 && (mat.flags&(1|32))!=0){unknownAt=min(unknownAt,distance);continue;}
        if((mat.flags&1)!=0 && Extensions.SampleLevel(LinearWrap,float3(textureUv(tri,bary),mat.atlasIndex),0).z<.5)continue;
        if((mat.flags&32)!=0){
            float3 a=mul(float4(Vertices[tri.a].p,1),inst.world).xyz,b=mul(float4(Vertices[tri.b].p,1),inst.world).xyz,c=mul(float4(Vertices[tri.c].p,1),inst.world).xyz;
            float3 gn=normalize(cross(b-a,c-a));if((mat.flags&64)!=0&&dot(gn,d)>=0)continue;float3 tr=thinTransmission(mat,tri,bary);bool duplicate=false;
            [loop]for(uint j=0;j<8;j++)if(j<layers && abs(distances[j]-distance)<=max(1e-5,rayError(o+d*distance)*2) && abs(dot(normals[j],gn))>.999){transmissions[j]=min(transmissions[j],tr);duplicate=true;}
            if(!duplicate){if(layers==8)unknown=true;else{distances[layers]=distance;normals[layers]=gn;transmissions[layers]=tr;layers++;}}
            continue;
        }
        if((mat.flags&(2|16))!=0){unknownAt=min(unknownAt,distance);continue;}
        opaque=true;blocker=min(blocker,distance);q.CommitNonOpaqueTriangleHit();
        if(!nearest){q.Abort();ShadowResult result=(ShadowResult)0;result.status=1;result.blocker=distance;result.quality=1;return result;}
    }
    unknown=unknown||unknownAt<(opaque?blocker:maximum);
    if(opaque){ShadowResult result=(ShadowResult)0;result.status=1;result.blocker=blocker;result.quality=unknown?1:2;return result;}
    if(unknown)return failure(2);
    ShadowResult result=(ShadowResult)0;result.transmission=1;result.blocker=maximum;result.quality=layers>0?2:0;
    [loop]for(uint j=0;j<8;j++)if(j<layers){result.transmission*=transmissions[j];result.blocker=min(result.blocker,distances[j]);}
    return result;
}
#else
ShadowResult traceVisibility(float3 o,float3 d,float maximum,bool nearest){
    if(InstanceCount==0||dot(o,o)>256*256)return failure(2);
    float horizon=sphereExit(o,d);bool unknown=maximum>horizon;maximum=min(maximum,horizon);
    float distances[8];float3 normals[8],transmissions[8];uint layers=0;
    bool opaque=false,overflow=false;float blocker=maximum;uint stack[64],sp=1,visits=0,intersections=0;stack[0]=TlasRoot;
    [loop]while(sp>0){
        if(++visits>8192){overflow=true;break;}uint ni=stack[--sp];if(ni>=NodeCount){overflow=true;break;}
        Node node=Nodes[ni];float nt;if(!box(o,d,node.lo,node.hi,nearest&&opaque?blocker:maximum,nt))continue;
        if(node.count==0){if(sp+2>64){overflow=true;break;}stack[sp++]=node.left;stack[sp++]=node.left+1;continue;}
        [loop]for(uint k=node.left;k<node.left+node.count;k++){
            if(k>=InstanceCount){overflow=true;break;}Instance inst=Instances[k];
            if(!box(o,d,inst.lo,inst.hi,nearest&&opaque?blocker:maximum,nt))continue;
            if(inst.root==0xffffffff){unknown=true;continue;}
            float3 localO=mul(float4(o,1),inst.inverse).xyz,localD=mul(float4(d,0),inst.inverse).xyz;
            uint bs[64],bp=1;bs[0]=inst.root;
            [loop]while(bp>0){
                if(++visits>8192){overflow=true;break;}uint bi=bs[--bp];if(bi>=NodeCount){overflow=true;break;}
                Node bn=Nodes[bi];if(!box(localO,localD,bn.lo,bn.hi,nearest&&opaque?blocker:maximum,nt))continue;
                if(bn.count==0){if(bp+2>64){overflow=true;break;}bs[bp++]=bn.left;bs[bp++]=bn.left+1;continue;}
                [loop]for(uint ti=bn.left;ti<bn.left+bn.count;ti++){
                    if(++intersections>4096||ti>=TriangleCount){overflow=true;break;}Triangle tri=Triangles[ti];float distance;float2 bary;
                    if(!triIndex(localO,localD,tri,nearest&&opaque?blocker:maximum,distance,bary))continue;
                    uint mi=inst.materialBase+tri.material;if(mi>=MaterialCount){unknown=true;continue;}Material mat=Materials[mi];
                    if((mat.flags&8)!=0 && (mat.flags&(1|32))!=0){unknown=true;continue;}
                    if((mat.flags&1)!=0 && Extensions.SampleLevel(LinearWrap,float3(textureUv(tri,bary),mat.atlasIndex),0).z<.5)continue;
                    if((mat.flags&32)!=0){
                        float3 a=mul(float4(Vertices[tri.a].p,1),inst.world).xyz,b=mul(float4(Vertices[tri.b].p,1),inst.world).xyz,c=mul(float4(Vertices[tri.c].p,1),inst.world).xyz;
                        float3 gn=normalize(cross(b-a,c-a));if((mat.flags&64)!=0&&dot(gn,d)>=0)continue;float3 tr=thinTransmission(mat,tri,bary);bool duplicate=false;
                        [loop]for(uint j=0;j<8;j++)if(j<layers && abs(distances[j]-distance)<=max(1e-5,rayError(o+d*distance)*2) && abs(dot(normals[j],gn))>.999){transmissions[j]=min(transmissions[j],tr);duplicate=true;}
                        if(!duplicate){if(layers==8)unknown=true;else{distances[layers]=distance;normals[layers]=gn;transmissions[layers]=tr;layers++;}}
                        continue;
                    }
                    if((mat.flags&(2|16))!=0){unknown=true;continue;}
                    opaque=true;blocker=min(blocker,distance);
                    if(!nearest){ShadowResult result=(ShadowResult)0;result.status=1;result.blocker=distance;result.quality=1;return result;}
                }
                if(overflow)break;
            }
            if(overflow)break;
        }
        if(overflow)break;
    }
    // A known opaque blocker certifies zero even if the remaining guide query failed.
    if(opaque){ShadowResult result=(ShadowResult)0;result.status=1;result.blocker=blocker;result.quality=overflow||unknown?1:2;return result;}
    if(overflow)return failure(3);if(unknown)return failure(2);
    ShadowResult result=(ShadowResult)0;result.transmission=1;result.blocker=maximum;result.quality=layers>0?2:0;
    [loop]for(uint j=0;j<8;j++)if(j<layers){result.transmission*=transmissions[j];result.blocker=min(result.blocker,distances[j]);}
    return result;
}
#endif
ShadowResult shadow(float3 pos,float3 geometric,float error,float3 d,float distance,bool infinite,bool nearest,float endpointError){
    if((ShadowFlags&1)!=0){ShadowResult r=(ShadowResult)0;r.transmission=1;return r;}
    if(CollectStatistics!=0){uint unused;InterlockedAdd(Statistics[0],1,unused);}
    float3 origin=surfaceOffset(pos,geometric,d,error);float maximum=128;
    if(!infinite){float3 target=pos+d*distance,delta=target-origin;float lengthToLight=length(delta);float bound=max(endpointError,rayError(target));if(distance<=2*(biasFor(pos,error)+bound))return failure(2);d=delta/max(lengthToLight,1e-8);maximum=max(1e-7,lengthToLight-bound);}
    return traceVisibility(origin,d,maximum,nearest);
}
ShadowResult lampShadow(Light l,float2 uv,float3 pos,float3 gn,float error,float3 d,float distance,bool nearest){
    if(l.kind<2 && LampRadius>0){float radius=min(LampRadius,distance*.25),r=sqrt(uv.x)*radius,phi=2*PI*uv.y;
        float3 target=pos+d*distance+basis(d,float3(r*cos(phi),r*sin(phi),0)),delta=target-pos;distance=length(delta);d=delta/max(distance,1e-8);}
    float endpointError=l.kind==2?triangleError(Triangles[l.triIndex],Instances[l.instance]):rayError(l.p);
    return shadow(pos,gn,error,d,distance,l.kind==3,nearest,endpointError);
}
float farSun(float3 p){float4 c=mul(float4(p,1),FarSunMatrix);float3 uvz=c.xyz/c.w;if(uvz.z<0||uvz.z>1)return -1;
    uvz.xy=float2(c.x/c.w*.5+.5,.5-c.y/c.w*.5);if(any(uvz.xy<0)||any(uvz.xy>1))return -1;float depth=FarSunDepth.Load(int3(min(uint2(uvz.xy*2048),2047),0));return uvz.z-5e-6<=depth?1:0;}
ShadowResult sunShadow(float3 pos,float3 gn,float error,float2 uv,bool nearest){
    float3 axis=normalize(SunDirection.xyz);float delta=2*pow(sin(SolarRadius*.5),2)*uv.x,ct=1-delta,st=sqrt(max(0,delta*(2-delta))),phi=2*PI*uv.y;
    float3 d=basis(axis,float3(st*cos(phi),st*sin(phi),ct));
    if(FarSunValid==0)return shadow(pos,gn,error,d,128,true,nearest,0);
    if((ShadowFlags&1)!=0){ShadowResult r=(ShadowResult)0;r.transmission=1;return r;}
    if(CollectStatistics!=0){uint unused;InterlockedAdd(Statistics[0],1,unused);}
    float3 origin=surfaceOffset(pos,gn,d,error);float endpoint=sphereExit(origin,d);
    ShadowResult r=traceVisibility(origin,d,max(1e-7,endpoint-rayError(origin+d*endpoint)),nearest);
    float far=farSun(pos);if(far==0){r.transmission=0;r.status=1;return r;}if(far<0&&r.status!=1)return failure(2);r.transmission*=max(far,0);return r;
}
// Legacy fixture adapter; production transport consumes RGB results.
bool visible(float3 pos,float3 n,float3 d,float distance,bool infinite,out bool valid){ShadowResult r=shadow(pos,n,rayError(pos),d,distance,infinite,false,0);valid=r.status<2;return any(r.transmission>0);}
#endif
