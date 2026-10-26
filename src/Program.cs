using System;
using System.Windows.Forms;
namespace C500GDesktop {
 internal static class Program {
  [STAThread] private static void Main(string[] args) {
   if(args.Length>0&&args[0]=="--ptz-worker"){Environment.ExitCode=PtzWorkerProcess.RunWorker(args);return;}
   Application.EnableVisualStyles();
   Application.SetCompatibleTextRenderingDefault(false);
   try {
    CameraStore store=new CameraStore();
    // A fresh installation starts with no camera profiles.
    ViewerForm viewer=new ViewerForm(new AccountPtzController(),store);
    viewer.Shown+=async delegate{await viewer.StartAccountAsync(Array.Exists(args,a=>a=="--login"));};
    Application.Run(viewer);
   } catch(Exception) {
    MessageBox.Show("앱을 시작하지 못했습니다. 앱 구성 파일 및 사용자 설정 폴더 접근 권한을 확인해 주세요.","C500G Desk",MessageBoxButtons.OK,MessageBoxIcon.Error);
   }
  }
 }
}
