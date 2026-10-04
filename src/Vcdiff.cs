using System;using System.IO;using System.Collections.Generic;
namespace SO4KoreanPatcher
{
    // RFC 3284 default code table, with xdelta's per-window Adler-32 extension.
    // Generated patches use -a -A -S none: no secondary compression or custom table.
    internal static class Vcdiff
    {
        private struct Instruction { internal int Kind, Size, Mode; internal Instruction(int k,int s,int m){Kind=k;Size=s;Mode=m;} }
        private static readonly Instruction[][] Table=MakeTable();
        private sealed class Reader
        {
            internal readonly byte[] Bytes; internal int Position; internal readonly int End;
            internal Reader(byte[] b,int p,int n){Storage.Require(p>=0&&n>=0&&p<=b.Length-n,"xdelta 구간이 잘렸습니다.");Bytes=b;Position=p;End=p+n;}
            internal int Byte(){Storage.Require(Position<End,"xdelta 데이터가 잘렸습니다.");return Bytes[Position++];}
            internal int Number(){long n=0;for(int i=0;i<5;i++){int b=Byte();n=(n<<7)|(uint)(b&127);Storage.Require(n<=int.MaxValue,"xdelta 정수가 범위를 벗어났습니다.");if((b&128)==0)return (int)n;}throw new InvalidDataException("xdelta 정수가 너무 깁니다.");}
            internal Reader Part(int n){Storage.Require(n>=0&&Position<=End-n,"xdelta 섹션 길이가 잘못되었습니다.");var r=new Reader(Bytes,Position,n);Position+=n;return r;}
            internal bool Done {get{return Position==End;}}
        }
        private static Instruction[][] MakeTable()
        {
            var t=new List<Instruction[]>();Action<int,int,int> one=(k,s,m)=>t.Add(new[]{new Instruction(k,s,m)});
            one(2,0,0);for(int n=0;n<=17;n++)one(1,n,0);
            for(int mode=0;mode<9;mode++){one(3,0,mode);for(int n=4;n<=18;n++)one(3,n,mode);}
            for(int mode=0;mode<6;mode++)for(int a=1;a<=4;a++)for(int c=4;c<=6;c++)t.Add(new[]{new Instruction(1,a,0),new Instruction(3,c,mode)});
            for(int mode=6;mode<9;mode++)for(int a=1;a<=4;a++)t.Add(new[]{new Instruction(1,a,0),new Instruction(3,4,mode)});
            for(int mode=0;mode<9;mode++)t.Add(new[]{new Instruction(3,4,mode),new Instruction(1,1,0)});
            if(t.Count!=256)throw new InvalidOperationException();return t.ToArray();
        }
        internal static byte[] Decode(byte[] source,byte[] delta,int expectedLength,int maximumLength=64*1024*1024)
        {
            Storage.Require(maximumLength>0&&maximumLength<=128*1024*1024&&expectedLength>=0&&expectedLength<=maximumLength,"xdelta 출력 길이가 잘못되었습니다.");
            var input=new Reader(delta,0,delta.Length);Storage.Require(input.Byte()==0xd6&&input.Byte()==0xc3&&input.Byte()==0xc4&&input.Byte()==0,"xdelta VCDIFF 헤더가 아닙니다.");
            int header=input.Byte();Storage.Require((header&~4)==0,"지원하지 않는 xdelta 압축 또는 코드표입니다.");if((header&4)!=0)input.Part(input.Number());
            var output=new byte[expectedLength];int total=0;
            while(!input.Done)
            {
                int flags=input.Byte();Storage.Require((flags&~7)==0&&(flags&3)!=3,"xdelta 창 플래그가 잘못되었습니다.");
                int sourceLength=0,sourcePosition=0;bool targetSource=(flags&2)!=0;
                if((flags&3)!=0){sourceLength=input.Number();sourcePosition=input.Number();int limit=targetSource?total:source.Length;Storage.Require(sourceLength<=limit&&sourcePosition<=limit-sourceLength,"xdelta 원본 참조가 범위를 벗어났습니다.");}
                var window=input.Part(input.Number());int count=window.Number();Storage.Require(count<=expectedLength-total,"xdelta 출력이 지정 길이를 초과했습니다.");
                Storage.Require(window.Byte()==0,"지원하지 않는 xdelta 추가 압축입니다.");int dataSize=window.Number(),codeSize=window.Number(),addressSize=window.Number();uint checksum=0;
                if((flags&4)!=0)for(int i=0;i<4;i++)checksum=(checksum<<8)|(uint)window.Byte();
                var data=window.Part(dataSize);var codes=window.Part(codeSize);var addresses=window.Part(addressSize);Storage.Require(window.Done,"xdelta 창 길이가 맞지 않습니다.");
                int[] near=new int[4],same=new int[768];int next=0,done=0;
                while(!codes.Done)
                {
                    foreach(var instruction in Table[codes.Byte()])
                    {
                        int n=instruction.Size==0?codes.Number():instruction.Size;Storage.Require(n<=count-done,"xdelta 명령이 출력 영역을 초과했습니다.");
                        if(instruction.Kind==1)
                        {var bytes=data.Part(n);Buffer.BlockCopy(delta,bytes.Position,output,total+done,n);}
                        else if(instruction.Kind==2)
                        {byte value=(byte)data.Byte();for(int i=0;i<n;i++)output[total+done+i]=value;}
                        else
                        {
                            long address;int mode=instruction.Mode;
                            if(mode==0)address=addresses.Number();else if(mode==1)address=(long)sourceLength+done-addresses.Number();
                            else if(mode<6)address=(long)near[mode-2]+addresses.Number();else address=same[(mode-6)*256+addresses.Byte()];
                            Storage.Require(address>=0&&address<(long)sourceLength+done,"xdelta 복사 주소가 잘못되었습니다.");int a=(int)address;
                            near[next]=a;next=(next+1)%4;same[a%768]=a;
                            if(a<sourceLength){Storage.Require(n<=sourceLength-a,"xdelta 원본 복사가 경계를 넘습니다.");Buffer.BlockCopy(targetSource?output:source,sourcePosition+a,output,total+done,n);}
                            else {int start=a-sourceLength;for(int i=0;i<n;i++)output[total+done+i]=output[total+start+i];}
                        }
                        done+=n;
                    }
                }
                Storage.Require(done==count&&data.Done&&addresses.Done,"xdelta 명령·데이터 길이가 맞지 않습니다.");
                if((flags&4)!=0)Storage.Require(Adler(output,total,count)==checksum,"xdelta 출력 체크섬이 다릅니다.");total+=count;
            }
            Storage.Require(total==expectedLength,"xdelta 최종 길이가 다릅니다.");return output;
        }
        private static uint Adler(byte[] b,int start,int count)
        {uint a=1,c=0;int end=start+count;for(int p=start;p<end;){int limit=Math.Min(end,p+5552);for(;p<limit;p++){a+=b[p];c+=a;}a%=65521;c%=65521;}return(c<<16)|a;}
    }
}
