using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace C500GDesktop {
 internal sealed class SavedAccount:IDisposable {
  internal string Username{get;private set;}
  internal string Password{get;private set;}
  internal SavedAccount(string username,string password){Username=username;Password=password;}
  public void Dispose(){Username=null;Password=null;}
 }
 internal sealed class AutoLoginStore {
  private readonly string path;
  private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("C500GDesk.AccountAutoLogin.v1");
  internal AutoLoginStore(string path){this.path=Path.GetFullPath(path);}
  internal bool HasSavedCredentials{get{return File.Exists(path);}}
  internal SavedAccount Load(out string warning){
   warning=null;byte[] encrypted=null,plain=null;
   try{
    using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
     if(file.Length==0||file.Length>16384)throw new InvalidDataException();
     encrypted=new byte[(int)file.Length];int count=0,read;
     while(count<encrypted.Length&&(read=file.Read(encrypted,count,encrypted.Length-count))>0)count+=read;
     if(count!=encrypted.Length)throw new InvalidDataException();
    }
    plain=ProtectedData.Unprotect(encrypted,Entropy,DataProtectionScope.CurrentUser);
    using(var stream=new MemoryStream(plain,false))using(var reader=new BinaryReader(stream,Encoding.UTF8)){
     if(reader.ReadInt32()!=1)throw new InvalidDataException();
     string username=reader.ReadString(),password=reader.ReadString();
     if(!Valid(username,password)||stream.Position!=stream.Length)throw new InvalidDataException();
     return new SavedAccount(username,password);
    }
   }catch(FileNotFoundException){return null;}
   catch(DirectoryNotFoundException){return null;}
   catch(Exception ex){if(!StorageError(ex))throw;warning="저장된 자동로그인 정보를 읽지 못했습니다. 계정과 비밀번호를 다시 입력해 주세요.";return null;}
   finally{Clear(encrypted);Clear(plain);}
  }
  internal bool Save(string username,string password,out string warning){
   warning=null;if(!Valid(username,password))throw new ArgumentException("Invalid account credentials");
   byte[] plain=null,encrypted=null;string temp=null;
   try{
    using(var stream=new MemoryStream()){
     try{
      using(var writer=new BinaryWriter(stream,Encoding.UTF8,true)){writer.Write(1);writer.Write(username);writer.Write(password);writer.Flush();}
      plain=stream.ToArray();
     }finally{Clear(stream.GetBuffer());}
    }
    encrypted=ProtectedData.Protect(plain,Entropy,DataProtectionScope.CurrentUser);
    Directory.CreateDirectory(Path.GetDirectoryName(path));temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
    using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(encrypted,0,encrypted.Length);file.Flush(true);}
    if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    return true;
   }catch(Exception ex){if(!StorageError(ex))throw;warning="로그인은 완료됐지만 자동로그인 정보를 저장하지 못했습니다. 다음 실행 때 다시 로그인해 주세요.";return false;}
   finally{Clear(plain);Clear(encrypted);if(temp!=null)try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
  }
  internal bool ClearSaved(out string warning){
   warning=null;
   try{File.Delete(path);return true;}
   catch(DirectoryNotFoundException){return true;}
   catch(Exception ex){if(!StorageError(ex))throw;warning="저장된 자동로그인 정보를 지우지 못했습니다. 다음 실행 때 자동로그인이 시도될 수 있습니다.";return false;}
  }
  private static bool Valid(string username,string password){return !String.IsNullOrWhiteSpace(username)&&username.Length<=254&&!String.IsNullOrEmpty(password)&&password.Length<=256;}
  private static bool StorageError(Exception ex){return ex is InvalidDataException||ex is IOException||ex is UnauthorizedAccessException||ex is CryptographicException||ex is System.Security.SecurityException||ex is FormatException||ex is ArgumentException;}
  private static void Clear(byte[] value){if(value!=null)Array.Clear(value,0,value.Length);}
 }
}
