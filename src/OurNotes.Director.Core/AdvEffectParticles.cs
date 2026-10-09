using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Native world-space billboard geometry from engine/particles.js. Simulation remains in AdvParticles.
public sealed class AdvEffectParticles
{
    readonly JsonNode renderer;
    public AdvParticles System{get;}
    public AdvTransform Transform{get;}
    public IReadOnlyList<JsonNode> Materials{get;}
    public JsonNode? Material=>Materials.FirstOrDefault();
    public int SortingOrder{get;set;}
    public int SortingLayer=>(int)Actor.N(renderer["m_SortingLayer"]);
    public Vector3 WorldCenter()
    {
        if(System.Particles.Count==0)return Transform.WorldPosition;
        var matrix=Transform.WorldMatrix();int scaling=(int)Actor.N(System.Data["scalingMode"]);
        if(scaling!=0){var q=Transform.Rotation;for(var parent=Transform.Parent;parent is not null;parent=parent.Parent)q=parent.Rotation*q;matrix=new AdvTransform{Position=Transform.WorldPosition,Rotation=q,Scale=scaling==1?Transform.Scale:Vector3.One}.LocalMatrix();}
        var low=new Vector3(float.PositiveInfinity);var high=new Vector3(float.NegativeInfinity);
        foreach(var particle in System.Particles){var p=AdvMatrix.Transform(matrix,new(particle.Position,1));var v=Actor.N(System.Data["moveWithTransform"])==1?particle.Position:new Vector3(p.X,p.Y,p.Z);low=Vector3.Min(low,v);high=Vector3.Max(high,v);}
        // The reference expands both sides equally by the largest extent, leaving this centre unchanged.
        return(low+high)*.5f;
    }
    public bool Visible=>Actor.B(renderer["m_Enabled"])&&Transform.ActiveInHierarchy&&System.Particles.Count>0&&(int)Actor.N(renderer["m_RenderMode"])!=5&&Material is not null;
    public sealed record Mesh(Dictionary<string,float[]> Attributes,uint[] Indices,float[] Matrix,IReadOnlyList<uint[]>? Submeshes=null,bool UnityUvs=false);
    static Vector3 Normal(Vector3 v)=>v.LengthSquared()>1e-24f?Vector3.Normalize(v):Vector3.Zero;
    static Vector3 Column(float[] m,int i)=>new(m[i*4],m[i*4+1],m[i*4+2]);
    public AdvEffectParticles(AdvParticles system,AdvTransform transform,JsonNode renderer)
    {System=system;Transform=transform;this.renderer=renderer;Materials=renderer["m_Materials"]?.AsArray().OfType<JsonNode>().ToArray()??[];SortingOrder=(int)Actor.N(renderer["m_SortingOrder"]);system.RendererFlip=Actor.B(renderer["m_Enabled"])?AdvPrefab.Vector(renderer["m_Flip"]):Vector3.Zero;}
    public Mesh Geometry(float[] cameraWorld,float[]? projection=null,Vector3 cameraVelocity=default)
    {
        int mode=(int)Actor.N(renderer["m_RenderMode"]),alignment=(int)Actor.N(renderer["m_RenderAlignment"]);
        if(mode is not (0 or 1 or 2 or 3 or 4))throw new NotSupportedException("Effect particle render mode: "+mode);
        var sourceMesh=mode==4?renderer["m_Mesh"]??throw new InvalidDataException("Particle mesh missing"):null;
        if(mode==1&&(Actor.B(renderer["m_FreeformStretching"])||Actor.N(renderer["m_Pivot"]?["z"])!=0))throw new NotSupportedException("Effect freeform stretching or stretched pivot Z");
        if(mode==1&&AdvPrefab.Vector(renderer["m_Flip"])!=Vector3.Zero)throw new NotSupportedException("Effect stretched billboard flip");
        if(Actor.B(System.Data["ShapeModule"]?["enabled"])&&Actor.B(System.Data["ShapeModule"]?["alignToDirection"])){if(mode is not (0 or 4)||alignment==1)throw new NotSupportedException("Effect shape alignment for this renderer mode");alignment=2;}
        bool world=Actor.N(System.Data["moveWithTransform"])==1;
        if(Actor.N(System.Data["moveWithTransform"]) is not (0 or 1))throw new NotSupportedException("Effect particle custom simulation");
        var matrix=Transform.WorldMatrix();var rotation=Transform.Rotation;for(var parent=Transform.Parent;parent is not null;parent=parent.Parent)rotation=parent.Rotation*rotation;
        var basis=new AdvTransform{Rotation=rotation}.LocalMatrix();var rx=Column(basis,0);var ry=Column(basis,1);var rz=Column(basis,2);
        int scaling=(int)Actor.N(System.Data["scalingMode"]);var scale=scaling==1?Transform.Scale:scaling==2?Vector3.One:new(Column(matrix,0).Length(),Column(matrix,1).Length(),Column(matrix,2).Length());
        Vector3 InverseRotation(Vector3 v)=>new(Vector3.Dot(v,rx),Vector3.Dot(v,ry),Vector3.Dot(v,rz));
        Vector3 Rotate(Vector3 v)=>rx*v.X+ry*v.Y+rz*v.Z;
        Vector3 FromWorld(Vector3 v)=>world?Rotate(InverseRotation(v)*scale):InverseRotation(v);
        float Along(Vector3 v)=>Rotate(InverseRotation(v)*scale).Length();
        var origin=Column(matrix,3);var camera=Column(cameraWorld,3);var cr=Normal(Column(cameraWorld,0));var cu=Normal(Column(cameraWorld,1));var cf=Normal(Column(cameraWorld,2));
        Vector3 World(AdvParticles.Particle p){if(world)return p.Position;if(scaling!=0)return origin+Rotate(p.Position*scale);var w=AdvMatrix.Transform(matrix,new(p.Position,1));return new(w.X,w.Y,w.Z);}
        int sort=(int)Actor.N(renderer["m_SortMode"]);
        IEnumerable<AdvParticles.Particle> sorted=sort switch{
            0=>System.Particles,1=>System.Particles.OrderByDescending(p=>Vector3.DistanceSquared(World(p),camera)),
            2=>System.Particles.OrderBy(p=>p.Age),3=>System.Particles.OrderByDescending(p=>p.Age),
            4=>System.Particles.OrderByDescending(p=>Vector3.Dot(World(p)-camera,cf)),5=>System.Particles.OrderBy(p=>Vector3.DistanceSquared(World(p),camera)),6=>System.Particles.OrderBy(p=>Vector3.Dot(World(p)-camera,cf)),
            _=>throw new NotSupportedException("Effect particle sort mode: "+sort)};
        var particles=sorted.ToArray();int count=particles.Length,nv=sourceMesh?["vertices"]!.AsArray().Count??4;
        var positions=new float[count*nv*3];var normals=new float[count*nv*3];var colors=new float[count*nv*4];var uv=new float[count*nv*2];var indices=new uint[count*6];
        var pivot=AdvPrefab.Vector(renderer["m_Pivot"]);
        for(int i=0;i<count;i++){
            var p=particles[i];var w=World(p);Vector3 right,up,normal;
            if(sourceMesh is not null){
                bool local=alignment==2;
                if(alignment==1){right=Vector3.UnitX;up=Vector3.UnitY;normal=Vector3.UnitZ;}
                else if(alignment==3){normal=Normal(w-camera);right=Normal(Vector3.Cross(cu,normal));up=Vector3.Cross(normal,right);}
                else if(alignment is 0 or 2){right=cr;up=cu;normal=cf;}
                else throw new NotSupportedException("Effect mesh alignment: "+alignment);
                Vector3 Orient(Vector3 v){if(!local)return FromWorld(right*v.X+up*v.Y+normal*v.Z);if(p.Alignment is not null)v=p.Alignment[0]*v.X+p.Alignment[1]*v.Y+p.Alignment[2]*v.Z;return world?Rotate(v*scale):v;}
                bool rotation3D=Actor.B(System.Data["InitialModule"]?["rotation3D"]);
                var q=Quaternion.CreateFromYawPitchRoll(rotation3D?p.Rotation.Y:0,rotation3D?p.Rotation.X:0,p.Rotation.Z);
                var dimensions=System.Size(p);var tint=System.Color(p);var tileUv=System.Tile(p);
                static Vector3 Read(JsonNode n,Vector3 fallback)=>new(Actor.N(n[0],fallback.X),Actor.N(n[1],fallback.Y),n.AsArray().Count>2?Actor.N(n[2],fallback.Z):fallback.Z);
                for(int j=0;j<nv;j++){
                    var v=Read(sourceMesh["vertices"]![j]!,Vector3.Zero);var norm=sourceMesh["normals"] is JsonArray ns?Read(ns[j]!,Vector3.UnitZ):Vector3.UnitZ;
                    var coord=sourceMesh["uv0"] is JsonArray us?Read(us[j]!,Vector3.Zero):Vector3.Zero;
                    var point=p.Position+Orient(Vector3.Transform((v*p.Flip+pivot)*dimensions,q));var nrm=Normal(Orient(Vector3.Transform(norm,q)));int vertex=i*nv+j;
                    for(int k=0;k<3;k++){positions[vertex*3+k]=point[k];normals[vertex*3+k]=nrm[k];}
                    for(int k=0;k<4;k++)colors[vertex*4+k]=MathF.Floor(Math.Clamp(tint[k],0,1)*255+.5f)/255;
                    uv[vertex*2]=tileUv.X+coord.X*tileUv.Z;uv[vertex*2+1]=1-(tileUv.Y+coord.Y*tileUv.W);
                }
                continue;
            }
            if(mode==1){
                var velocity=p.Velocity+p.Animated;
                var worldVelocity=(world?velocity:Rotate(velocity*scale))-cameraVelocity*Actor.N(renderer["m_CameraVelocityScale"]);
                var direction=Normal(worldVelocity);var stretchedSize=System.Size(p);
                var lift=direction*(pivot.Y*stretchedSize.Y);
                Vector3 ScaledWorld(Vector3 d)=>Rotate(InverseRotation(d)*scale);
                float length=Actor.N(renderer["m_LengthScale"],2),speed=Actor.N(renderer["m_VelocityScale"]);
                var headWorld=w+ScaledWorld(lift);var tailWorld=headWorld-worldVelocity*speed-ScaledWorld(direction*(length*stretchedSize.Y));
                Vector3 View(Vector3 d)=>new(Vector3.Dot(d,cr),Vector3.Dot(d,cu),-Vector3.Dot(d,cf));
                var headView=View(headWorld-camera);var tailView=View(tailWorld-camera);
                var cross=new Vector2(headView.Z*tailView.Y-headView.Y*tailView.Z,headView.X*tailView.Z-headView.Z*tailView.X);
                cross=cross.LengthSquared()>0?Vector2.Normalize(cross):Vector2.Zero;
                var across=cr*cross.X+cu*cross.Y;float halfWidth=.5f*stretchedSize.X;
                float maxSize=Actor.N(renderer["m_MaxParticleSize"]),minSize=Actor.N(renderer["m_MinParticleSize"]);
                if(projection is not null&&(maxSize>0||minSize>0)){
                    float viewport=projection[15]==0?2*Vector3.Dot(w-camera,cf)/projection[5]:2/projection[5];float extent=Math.Abs(2*halfWidth)*Along(across);
                    if(extent>0&&viewport>0)halfWidth*=extent>maxSize*viewport?maxSize*viewport/extent:extent<minSize*viewport?minSize*viewport/extent:1;
                }
                var offset=FromWorld(across*halfWidth);var side=offset*pivot.X;var head=p.Position+FromWorld(lift)+side;
                var velocityPart=velocity;
                if(cameraVelocity!=Vector3.Zero){var inverse=InverseRotation(worldVelocity);velocityPart=world?worldVelocity:new(Math.Abs(scale.X)>1e-12?inverse.X/scale.X:0,Math.Abs(scale.Y)>1e-12?inverse.Y/scale.Y:0,Math.Abs(scale.Z)>1e-12?inverse.Z/scale.Z:0);}
                var tail=head-velocityPart*speed-FromWorld(direction*(length*stretchedSize.Y));
                Vector3[] corners=[head+offset,tail+offset,tail-offset,head-offset];var nrm=Normal(FromWorld(-cf));var tint=System.Color(p);var tileUv=System.Tile(p);
                for(int j=0;j<4;j++){
                    int vertex=i*4+j;for(int k=0;k<3;k++){positions[vertex*3+k]=corners[j][k];normals[vertex*3+k]=nrm[k];}
                    for(int k=0;k<4;k++)colors[vertex*4+k]=MathF.Floor(Math.Clamp(tint[k],0,1)*255+.5f)/255;
                    uv[vertex*2]=tileUv.X+(j is 1 or 2?tileUv.Z:0);uv[vertex*2+1]=1-(tileUv.Y+(j>=2?tileUv.W:0));
                }
                int[] triangles=[0,1,2,0,2,3];for(int k=0;k<6;k++)indices[i*6+k]=(uint)(i*4+triangles[k]);
                continue;
            }
            if(mode==2){right=Vector3.UnitX;up=Vector3.UnitZ;normal=Vector3.UnitY;}
            else if(mode==3){up=Vector3.UnitY;right=Normal(Vector3.Cross(up,cf));if(right==Vector3.Zero)right=cr;normal=Vector3.Cross(up,right);}
            else if(alignment==1){right=Vector3.UnitX;up=Vector3.UnitY;normal=-Vector3.UnitZ;}
            else if(alignment==2){right=p.Alignment is null?rx:Rotate(p.Alignment[0]);up=p.Alignment is null?ry:Rotate(p.Alignment[1]);normal=p.Alignment is null?-rz:-Rotate(p.Alignment[2]);}
            else if(alignment==3){var f=Normal(w-camera);right=Normal(Vector3.Cross(cu,f));up=Vector3.Cross(f,right);normal=-f;}
            else if(alignment==0){right=cr;up=cu;normal=-cf;}
            else throw new NotSupportedException("Effect particle alignment: "+alignment);
            float cos=MathF.Cos(p.Rotation.Z),sin=MathF.Sin(p.Rotation.Z);var r=right*cos-up*sin;var u=right*sin+up*cos;var size=System.Size(p);
            float maximum=Actor.N(renderer["m_MaxParticleSize"]),minimum=Actor.N(renderer["m_MinParticleSize"]);
            if(projection is not null&&(maximum>0||minimum>0)){
                float viewport=projection[15]==0?2*Vector3.Dot(w-camera,cf)/projection[5]:2/projection[5];float extent=Math.Max(Math.Abs(size.X)*Along(r),Math.Abs(size.Y)*Along(u));
                if(extent>0&&viewport>0){float k=extent>maximum*viewport?maximum*viewport/extent:extent<minimum*viewport?minimum*viewport/extent:1;size.X*=k;size.Y*=k;}
            }
            var color=System.Color(p);var tile=System.Tile(p);var n=Normal(FromWorld(normal));
            for(int j=0;j<4;j++){
                float x=j is 1 or 2?.5f:-.5f,y=j>=2?.5f:-.5f;
                var position=p.Position+FromWorld(r*((x*p.Flip.X+pivot.X)*size.X)+u*((y*p.Flip.Y+pivot.Y)*size.Y)-normal*(pivot.Z*size.Z));
                int vertex=i*4+j;for(int k=0;k<3;k++){positions[vertex*3+k]=position[k];normals[vertex*3+k]=n[k];}
                for(int k=0;k<4;k++)colors[vertex*4+k]=MathF.Floor(Math.Clamp(color[k],0,1)*255+.5f)/255;
                uv[vertex*2]=tile.X+(x+.5f)*tile.Z;uv[vertex*2+1]=1-(tile.Y+(y+.5f)*tile.W);
            }
            int start=i*4;int[] tri=[0,1,2,0,2,3];for(int k=0;k<6;k++)indices[i*6+k]=(uint)(start+tri[k]);
        }
        // _frame() uses simulation scaling for the render matrix, independently of hierarchy scaling.
        var renderMatrix=AdvMatrix.Identity();for(int k=0;k<3;k++){renderMatrix[k]=rx[k]*scale.X;renderMatrix[4+k]=ry[k]*scale.Y;renderMatrix[8+k]=rz[k]*scale.Z;renderMatrix[12+k]=origin[k];}
        List<uint[]>? submeshes=null;
        if(sourceMesh is not null){submeshes=[];foreach(var sub in sourceMesh["submeshes"]!.AsArray()){
            var raw=sub!.AsArray();var expanded=new uint[count*raw.Count];for(int i=0;i<count;i++)for(int j=0;j<raw.Count;j++){int index=(int)Actor.N(raw[j]);if(index<0||index>=nv)throw new InvalidDataException("Particle mesh index out of range");expanded[i*raw.Count+j]=checked((uint)(i*nv+index));}submeshes.Add(expanded);
        }indices=submeshes.FirstOrDefault()??[];}
        var attributes=new Dictionary<string,float[]>{{"in_POSITION0",positions},{"in_NORMAL0",normals},{"in_COLOR0",colors},{"in_TEXCOORD0",uv}};bool custom=Actor.B(renderer["m_UseCustomVertexStreams"]);
        if(custom)attributes=AdvParticleVertexStreams.Pack(renderer,System,particles,nv,attributes);
        return new(attributes,indices,world?AdvMatrix.Identity():scaling==0?matrix:renderMatrix,submeshes,custom);
    }
}
