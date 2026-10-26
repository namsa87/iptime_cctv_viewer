using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
namespace C500GDesktop {
 public sealed class CameraStore {
  private readonly string path;
  public string FilePath{get{return path;}}
  public CameraStore():this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"C500GDesktop","cameras.xml")){}
  public CameraStore(string path){this.path=path;}
  public List<CameraProfile> Load(out string warning){
   warning=null; if(!File.Exists(path))return new List<CameraProfile>();
   try{ using(FileStream stream=File.OpenRead(path)){
    List<CameraProfile> profiles=(List<CameraProfile>)new XmlSerializer(typeof(List<CameraProfile>)).Deserialize(stream);
    HashSet<string> ids=new HashSet<string>(StringComparer.Ordinal);
    foreach(CameraProfile p in profiles)if(String.IsNullOrEmpty(p.Id)||!ids.Add(p.Id)){p.Id=Guid.NewGuid().ToString("N");ids.Add(p.Id);}
    return profiles;
   }}catch(Exception ex){
    if(!(ex is IOException)&&!(ex is InvalidOperationException)&&!(ex is UnauthorizedAccessException))throw;
    warning="카메라 설정을 읽지 못했습니다. 기존 설정 파일은 보존했습니다.";return new List<CameraProfile>();
   }
  }
  public void Save(List<CameraProfile> profiles){
   Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{using(FileStream stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
     new XmlSerializer(typeof(List<CameraProfile>)).Serialize(stream,profiles);
    if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);
   }finally{if(File.Exists(temp))File.Delete(temp);}
  }
 }
}