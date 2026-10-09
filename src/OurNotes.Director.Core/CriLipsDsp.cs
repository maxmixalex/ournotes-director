// Port of ournotes-player's CRI Lips DSP (AGPL-3.0-only).
namespace OurNotes.Director.Core;

static class CriMath
{
    public static float Sin(float x){float s=x*x,t=s/-72+1;t=s/-42*t+1;t=s/-20*t+1;t=s/-6*t+1;return t*x;}
    public static float Cos(float x){float s=x*x,t=s/-56+1;t=s/-30*t+1;t=s/-12*t+1;return s*-.5f*t+1;}
    public static float SinLane(float x){float x2=x*x,x3=x2*x,x5=x3*x2,x7=x2*x5;return x2*x7*(1f/362880)+(x5*(1f/120)+(x-x3*(1f/6))-x7*(1f/5040));}
    public static float CosLane(float x){float x2=x*x,x4=x2*x2,x6=x4*x2;return x2*x6*(1f/40320)+(x4*(1f/24)+(1-x2*.5f)-x6*(1f/720));}
}
sealed class CriFft
{
    readonly int n;readonly List<(float[] R,float[] I,int Stride)> stages=[];
    readonly (float[] R,float[] I) lane2,lane3;
    readonly float[][] re,im;
    public CriFft(int n)
    {
        this.n=n;re=[new float[n],new float[n]];im=[new float[n],new float[n]];
        float theta=MathF.PI;for(int st=0;st<(int)Math.Log2(n)-2;st++){
            float c=CriMath.Cos(theta),s=CriMath.Sin(theta),r=1,i=0;int groups=1<<st;var wr=new float[groups];var wi=new float[groups];
            for(int j=0;j<groups;j++){wr[j]=r;wi[j]=i;float nr=c*r-s*i;i=s*r+c*i;r=nr;}
            stages.Add((wr,wi,(n>>3)>>st));theta*=.5f;
        }
        float a=theta,b=a*.5f;lane2=Lanes([0,0,a,a],a+a);lane3=Lanes([0,b,b+b,b*3],b*4);
    }
    (float[],float[]) Lanes(float[] angles,float rotation)
    {
        float c=CriMath.Cos(rotation),s=CriMath.Sin(rotation);int m=n>>3;var wr=new float[m*4];var wi=new float[m*4];
        for(int l=0;l<4;l++){float r=CriMath.CosLane(angles[l]),i=CriMath.SinLane(angles[l]);for(int k=0;k<m;k++){wr[k*4+l]=r;wi[k*4+l]=i;float nr=c*r-s*i;i=s*r+c*i;r=nr;}}
        return(wr,wi);
    }
    public void Transform(float[] input,float[] zero,float[] outputR,float[] outputI)
    {
        var sr=input;var si=zero;int k=0,half=n>>1;
        foreach(var st in stages){var dr=re[k&1];var di=im[k++&1];int len=st.Stride*4;
            for(int j=0;j<st.R.Length;j++){float r=st.R[j],w=-st.I[j];for(int e=0;e<len;e++){int a=2*j*len+e,b=a+len,o=j*len+e;float tr=r*sr[b]-w*si[b],ti=w*sr[b]+r*si[b];dr[o]=sr[a]+tr;di[o]=si[a]+ti;dr[o+half]=sr[a]-tr;di[o+half]=si[a]-ti;}}
            sr=dr;si=di;
        }
        Lane(sr,si,re[k&1],im[k&1],lane2,2,1);Lane(re[k&1],im[k&1],outputR,outputI,lane3,1,2);
    }
    void Lane(float[] sr,float[] si,float[] dr,float[] di,(float[] R,float[] I) tw,int bo,int es)
    {
        int half=n>>1;for(int i=0;i<n>>3;i++)for(int l=0;l<4;l++){int t=i*4+l,a=(2*i+(l>>1))*4+(l&1)*es,b=a+bo;float r=tw.R[t],w=-tw.I[t],tr=r*sr[b]-w*si[b],ti=w*sr[b]+r*si[b];dr[t]=sr[a]+tr;di[t]=si[a]+ti;dr[t+half]=sr[a]-tr;di[t+half]=si[a]-ti;}
    }
}
sealed class CriMel
{
    const float Min=1.1754943508222875e-38f;
    readonly int n,frameLength,hop,bands;readonly CriFft fft;
    readonly float[] window,queue,frame,zero,re,im,power;readonly List<(int Low,float[] Weights)> weights=[];
    int count,debt;
    public CriMel(int frameLength,int hop,int bands,int rate,float high,string type)
    {
        this.frameLength=frameLength;this.hop=hop;this.bands=bands;n=1;while(n<frameLength)n*=2;fft=new(n);
        window=new float[n];queue=new float[frameLength];frame=new float[n];zero=new float[n];re=new float[n];im=new float[n];power=new float[n/2];
        float WrappedCos(float a){if(a>MathF.PI){do a+=-MathF.Tau;while(a>MathF.PI);while(a<=-MathF.PI)a+=MathF.Tau;}return MathF.Cos(a);}
        for(int i=0;i<n;i++){if(type=="blackman-harris"){float t=i/(float)n,c1=WrappedCos(t*MathF.Tau),c2=WrappedCos(t*(float)(4*Math.PI)),c3=WrappedCos(t*(float)(6*Math.PI));window[i]=((c1*-.48829f+.35875f)+c2*.14128f)+c3*-.01168f;}
            else if(type=="hamming")window[i]=MathF.Cos((i+i)*MathF.PI/(n-1))*-.46f+.54f;else throw new NotSupportedException("CRI mel window: "+type);}
        float binWidth=rate/(float)n,melHigh=MathF.Log(high/700+1)*1127.0105f;
        float Edge(int i)=>(MathF.Exp(i*melHigh/(bands+1)/1127.0105f)-1)*700/binWidth;
        for(int m=0;m<bands;m++){float lo=Edge(m),c=Edge(m+1),hi=Math.Min(Edge(m+2),n/2);int low=(int)MathF.Floor(lo);var w=new float[(int)MathF.Ceiling(hi)-low];
            for(int j=0;j<w.Length;j++){float k=low+j,v=lo>=k?0:k<c?(k-lo)/(c-lo):k<=hi?1-(k-c)/(hi-c):0,s=CriMath.Sin(binWidth*k*.5f/high*MathF.PI),s2=s+s;w[j]=v*(s2*s2);}weights.Add((low,w));}
    }
    public void Initialize(){Array.Clear(queue);count=frameLength;debt=0;}
    int Drop(int k){int d=Math.Min(k,count);Array.Copy(queue,d,queue,0,count-d);count-=d;return d;}
    public void Shift(){int d=Drop(hop);debt+=hop-d;}
    public bool Put(ReadOnlySpan<float> pcm){int n=Math.Min(pcm.Length,frameLength-count);pcm[..n].CopyTo(queue.AsSpan(count));count+=n;int d=Drop(debt);debt-=d;return n-d==hop&&count>=frameLength;}
    public void Coefficients(float[] output)
    {
        Array.Clear(frame);Array.Copy(queue,0,frame,(n-frameLength)/2,frameLength);for(int i=0;i<n;i++)frame[i]*=window[i];fft.Transform(frame,zero,re,im);
        for(int k=0;k<n/2;k++)power[k]=Math.Max(Min,re[k]*re[k]+im[k]*im[k]);
        float sum=0;for(int m=0;m<bands;m++){float acc=0;var w=weights[m];for(int j=0;j<w.Weights.Length;j++)acc+=w.Weights[j]*power[w.Low+j];output[m]=MathF.Log10(Math.Max(Min,acc));sum+=output[m];}
        float mean=sum/bands;for(int m=0;m<bands;m++)output[m]-=mean;
    }
    public float Power(){float sum=0;foreach(float x in queue)sum+=x;float mean=sum/frameLength,acc=0;foreach(float x in queue){float d=x-mean;acc+=d*d;}return MathF.Log10(Math.Max(Min,acc/frameLength));}
}
sealed class CriBiquad
{
    readonly float[] v,st=new float[4];
    public CriBiquad(int sampleRate,float cutoff)
    {
        float fc=Math.Clamp(cutoff,10,sampleRate*.5f-100),angle=fc*MathF.Tau/sampleRate,s=MathF.Sin(angle),c=MathF.Cos(angle),alpha=s/2,omc=1-c,a0=alpha+1;
        float a1=c*-2/a0,b0=omc*.5f/a0,b1=omc/a0,b2=b0,a2=(1-alpha)/a0,aa=a1*a1,na1=-a1;
        float t7=b1-b0*a1,t11=b2*aa-b2*a2,t13=b2-b1*a1,t15=a2*a2-aa*a2,t12=a1*a2,t10=aa-a2,t14=t13*na1-b1*a2,t6=t12-t10*a1,t9=b2+(t7*na1-b0*a2);
        v=[0,0,0,b0,0,0,b0,t7,0,b0,t7,t9,b0,t7,t9,t9*na1-a2*t7,b2,b2*na1,t11,b2*t12-t11*a1,b1,t13,t14,t14*na1-a2*t13,-a2,t12,t15,t15*na1-a2*t12,na1,t10,t6,t6*na1-a2*t10];
    }
    public void Process(float[] x,float[] y,int n)
    {
        float xm2=st[0],xm1=st[1],ym2=st[2],ym1=st[3];
        for(int i=0;i<(n&~7);i+=4){for(int l=0;l<4;l++){float t=v[8+l]*x[i+1]+v[12+l]*x[i];t=v[4+l]*x[i+2]+t;t=v[l]*x[i+3]+t;t+=v[16+l]*xm2;t+=v[20+l]*xm1;t+=v[24+l]*ym2;y[i+l]=t+v[28+l]*ym1;}xm2=x[i+2];xm1=x[i+3];ym2=y[i+2];ym1=y[i+3];}
        st[0]=xm2;st[1]=xm1;st[2]=Math.Abs(ym2)<1e-24f?0:ym2;st[3]=Math.Abs(ym1)<1e-24f?0:ym1;
    }
}
sealed class CriResampler
{
    readonly float[] history=new float[9];readonly int step,minOut;int position;
    public CriResampler(int input,int output){step=Math.Max(1,Math.Min(input,output*8)*4096/output);minOut=step<4096?4096/step:0;}
    public int Need(int count)=>step==4096?count:(position+step*count+32768)>>12;
    public int Process(float[] input,int count,float[] output,int offset)
    {
        int pos=position,n=Math.Min(output.Length-offset,(count*4096-pos)/step-minOut);if(n<1)return 0;history[8]=input[0];int j=0;
        for(;j<n&&pos<0;j++,pos+=step){int p=pos+32768,idx=p>>12;float a=history[idx];output[offset+j]=a+((history[idx+1]-a)*(p&4095))*(1f/4096);}
        int main=((n-j)&~3)+j;for(;j<main;j++,pos+=step){int idx=pos>>12;float a=input[idx];output[offset+j]=a+((pos&4095)*(1f/4096))*(input[idx+1]-a);}
        for(;j<n;j++,pos+=step){int idx=pos>>12;float a=input[idx];output[offset+j]=a+((input[idx+1]-a)*(pos&4095))*(1f/4096);}
        if(count>=8)Array.Copy(input,count-8,history,0,8);else{Array.Copy(history,count,history,0,8-count);Array.Copy(input,0,history,8-count,count);}position=pos-count*4096;return n;
    }
}
