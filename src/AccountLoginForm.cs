using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace C500GDesktop {
 internal sealed class AccountLoginForm:Form {
  private readonly Func<string,string,CancellationToken,Task> authenticate;
  private readonly TextBox username=new TextBox(),password=new TextBox();
  private readonly Label status=new Label();
  private readonly Button login=new Button(),cancel=new Button();
  private readonly CheckBox remember=new CheckBox();
  private readonly AutoLoginStore credentialStore;
  private bool automaticAttempted;
  internal string PersistenceWarning{get;private set;}
  private CancellationTokenSource cancellation;private bool busy,closeRequested;
  internal AccountLoginForm(Func<string,string,CancellationToken,Task> authenticate):this(authenticate,null,false){}
  internal AccountLoginForm(Func<string,string,CancellationToken,Task> authenticate,AutoLoginStore credentialStore,bool automatic){
   this.authenticate=authenticate;this.credentialStore=credentialStore;Text="ipTIME CCTV 계정 로그인 · 1.4.2";ClientSize=new Size(460,450);
   Font=new Font("맑은 고딕",10F);BackColor=Color.White;ForeColor=Color.FromArgb(29,40,59);
   FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
   Controls.Add(new Label{Text="휴대폰 ipTIME CCTV 앱 계정",Font=new Font(Font,FontStyle.Bold),Location=new Point(24,22),Size=new Size(390,28)});
   Controls.Add(new Label{Text="카메라 RTSP 암호와 다른 계정입니다.",Location=new Point(24,54),Size=new Size(390,25),ForeColor=Color.DimGray});
   Controls.Add(new Label{Text="이메일 / 휴대전화",Location=new Point(24,91),Size=new Size(390,23)});
   username.Location=new Point(24,118);username.Size=new Size(392,28);username.MaxLength=254;Controls.Add(username);
   Controls.Add(new Label{Text="비밀번호",Location=new Point(24,158),Size=new Size(390,23)});
   password.Location=new Point(24,185);password.Size=new Size(392,28);password.UseSystemPasswordChar=true;password.MaxLength=256;Controls.Add(password);
   remember.Text="다음 실행부터 자동 로그인";remember.Location=new Point(24,226);remember.Size=new Size(392,28);remember.Enabled=credentialStore!=null;remember.Checked=automatic||(credentialStore!=null&&credentialStore.HasSavedCredentials);Controls.Add(remember);
   status.Location=new Point(24,267);status.Size=new Size(412,112);status.Text="체크하면 계정 정보를 이 Windows 사용자 계정으로 암호화해 저장합니다. 로그아웃하면 자동로그인도 해제됩니다.";status.ForeColor=Color.DimGray;Controls.Add(status);
   login.Text="로그인";login.Location=new Point(254,391);login.Size=new Size(86,36);login.Click+=async delegate{await SubmitAsync();};Controls.Add(login);
   cancel.Text="취소";cancel.Location=new Point(350,391);cancel.Size=new Size(86,36);cancel.Click+=delegate{if(busy){closeRequested=true;cancellation.Cancel();}else Close();};Controls.Add(cancel);
   AcceptButton=login;CancelButton=cancel;
   FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){e.Cancel=true;closeRequested=true;cancellation.Cancel();}};
   if(automatic)Shown+=async delegate{await RestoreAndSubmitAsync();};
  }
  private async Task RestoreAndSubmitAsync(){
   if(automaticAttempted||credentialStore==null)return;automaticAttempted=true;
   string warning;
   using(SavedAccount saved=credentialStore.Load(out warning)){
    if(saved==null){status.Text=warning??"저장된 계정이 없습니다. 로그인 정보를 입력해 주세요.";return;}
    username.Text=saved.Username;password.Text=saved.Password;
   }
   await SubmitAsync();
  }
  private async Task SubmitAsync(){
   if(busy)return;if(String.IsNullOrWhiteSpace(username.Text)||String.IsNullOrEmpty(password.Text)){status.Text="계정과 비밀번호를 입력해 주세요.";return;}
   busy=true;username.Enabled=password.Enabled=login.Enabled=remember.Enabled=false;cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(45));status.Text="계정을 확인하고 카메라 연결을 준비하고 있습니다…";status.ForeColor=Color.DimGray;bool success=false;
   try{
    await authenticate(username.Text.Trim(),password.Text,cancellation.Token);success=true;
    if(credentialStore!=null&&!closeRequested&&!cancellation.IsCancellationRequested){
     string warning;if(remember.Checked)credentialStore.Save(username.Text.Trim(),password.Text,out warning);else credentialStore.ClearSaved(out warning);
     PersistenceWarning=warning;
    }
   }
   catch(OperationCanceledException){status.Text=closeRequested?"로그인을 취소했습니다.":"연결 시간이 초과되었습니다. 네트워크를 확인하고 다시 로그인해 주세요.";}
   catch(CloudAccountException ex){status.Text=ex.Message+(String.IsNullOrEmpty(ex.SafeDetail)?"":"\r\n진단: "+ex.SafeDetail);status.ForeColor=Color.Firebrick;}
   catch(NativePtzException ex){status.Text=ex.Message;status.ForeColor=Color.Firebrick;}
   catch(Exception){status.Text="계정 연결을 완료하지 못했습니다. 네트워크와 연결 모듈 상태를 확인해 주세요.";status.ForeColor=Color.Firebrick;}
   finally{password.Clear();busy=false;username.Enabled=password.Enabled=login.Enabled=true;remember.Enabled=credentialStore!=null;cancellation.Dispose();cancellation=null;}
   if(success){DialogResult=DialogResult.OK;Close();}else if(closeRequested)Close();
  }
 }
}
