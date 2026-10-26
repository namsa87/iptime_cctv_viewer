using System;

using System.Collections.Generic;

using System.Drawing;

using System.IO;

using System.Linq;

using System.Runtime.InteropServices;

using System.Threading;

using System.Threading.Tasks;

using System.Windows.Forms;



namespace C500GDesktop

{

 public sealed class ViewerForm : Form, IMessageFilter

 {

  private static readonly Color Ink=Color.FromArgb(29,40,59), Muted=Color.FromArgb(105,119,138), Accent=Color.FromArgb(38,103,235), Border=Color.FromArgb(219,226,236);

  private readonly IPtzController ptz;

  private readonly CameraStore store;

  private readonly List<CameraProfile> cameras;

  private readonly List<VideoTile> tiles=new List<VideoTile>();

  private readonly Panel inactiveVideoHost=new Panel{Visible=false,Size=new Size(640,360)};

  private readonly Func<CameraProfile,IntPtr,IVideoPlayer> localVideoFactory;

  private CancellationTokenSource connectionBatch;

  private readonly Func<DateTime> utcNow;private bool reconnectBusy;

  private readonly List<Button> directionButtons=new List<Button>();

  private readonly SemaphoreSlim ptzGate=new SemaphoreSlim(1,1);

  private VlcEngine engine;

  private TableLayoutPanel grid;

  private ListBox cameraList;

  private Label selectedName, selectedAddress, ptzStatus, banner, countLabel, clockLabel;

  private Button addButton;private Label cameraHelp;private bool accountMode;

  private Button editButton, removeButton, connectButton, disconnectButton, stopButton;

  private ComboBox layoutBox;

  private CheckBox audioEnabled;

  private TrackBar audioVolume;

  private Label audioVolumeLabel;

  private System.Windows.Forms.Timer timer;

  private CameraProfile selected, movingCamera;

  private CancellationTokenSource motionCancellation;

  private long gesture;

  private Button heldPtzButton;

  private Keys heldPtzKey=Keys.None;

  private Task stoppingTask=Task.FromResult(0);

  private const int PtzRefreshMilliseconds=80;

  private readonly System.Diagnostics.Stopwatch motionClock=new System.Diagnostics.Stopwatch();

  private readonly List<string> motionTrace=new List<string>();

  private bool refreshing, closing, finalClose, initializedEngine;

  private int page;

  private int layoutCount=4;

  private Label pageLabel;

  private Button previousPage, nextPage;

  private Button accountButton;private Label accountStatus;private bool accountBusy;

  private bool settingsLoaded;

  private readonly AutoLoginStore autoLoginStore;private bool startupHandled;

  public ViewerForm(IPtzController ptz):this(ptz,new CameraStore()){}

  public ViewerForm(IPtzController ptz, CameraStore cameraStore):this(ptz,cameraStore,null){}

  internal ViewerForm(IPtzController ptz, CameraStore cameraStore,Func<CameraProfile,IntPtr,IVideoPlayer> localVideoFactory,Func<DateTime> utcNow=null)

  {

   this.ptz=ptz??new UnavailablePtzController();store=cameraStore;this.localVideoFactory=localVideoFactory??CreateLocalVideoPlayer;this.utcNow=utcNow??(()=>DateTime.UtcNow);

   autoLoginStore=new AutoLoginStore(store.FilePath+".account-login.dat");

   string warning;cameras=store.Load(out warning);settingsLoaded=warning==null;

   Text="C500G Desk · 다중 카메라 · 계정 영상 1.4.2";Name="C500GViewerForm";

   StartPosition=FormStartPosition.CenterScreen;Size=new Size(1450,900);MinimumSize=new Size(1120,720);

   Font=new Font("맑은 고딕",9F);BackColor=Color.FromArgb(240,243,248);ForeColor=Ink;AutoScaleMode=AutoScaleMode.Dpi;KeyPreview=true;

   BuildLayout();Controls.Add(inactiveVideoHost);RefreshCameraList();BuildGrid();UpdateSelection();

   if(warning!=null)ShowNotice(warning,true);

   timer=new System.Windows.Forms.Timer();timer.Interval=600;timer.Tick+=async delegate{RefreshState();await ReconnectDueAsync();};timer.Start();

   Shown+=delegate{if(settingsLoaded)ShowNotice("계정 로그인으로 등록된 카메라 영상을 볼 수 있습니다.",false);};

   Deactivate+=async delegate{await StopPtzAsync();};

   KeyDown+=OnViewerKeyDown;KeyUp+=async delegate(object sender,KeyEventArgs e){if(e.KeyCode==heldPtzKey&&heldPtzKey!=Keys.None){e.Handled=true;await StopPtzAsync();}};

   FormClosing+=OnFormClosing;

   Application.AddMessageFilter(this);

  }

  private Label LabelText(string text,int size,Color color,bool bold)

  {return new Label{Text=text,AutoSize=false,ForeColor=color,Font=new Font("맑은 고딕",size,bold?FontStyle.Bold:FontStyle.Regular),TextAlign=ContentAlignment.MiddleLeft};}

  private Button ButtonText(string text,bool primary)

  {

   Button b=new Button{Text=text,FlatStyle=FlatStyle.Flat,Height=36,BackColor=primary?Accent:Color.White,ForeColor=primary?Color.White:Ink,Cursor=Cursors.Hand,UseVisualStyleBackColor=false};

   b.FlatAppearance.BorderColor=primary?Accent:Border;b.FlatAppearance.BorderSize=1;return b;

  }

  private void BuildLayout()

  {

   TableLayoutPanel shell=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(0),Margin=new Padding(0)};

   shell.RowStyles.Add(new RowStyle(SizeType.Absolute,90));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,36));

   Controls.Add(shell);

   Panel header=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(26,16,26,14),Margin=new Padding(0)};

   Label wordmark=LabelText("C500G  Desk",21,Ink,true);wordmark.Location=new Point(26,13);wordmark.Size=new Size(320,36);header.Controls.Add(wordmark);

   Label sub=LabelText("MULTI CAMERA  /  LIVE VIEW",9,Muted,false);sub.Location=new Point(28,51);sub.Size=new Size(360,22);header.Controls.Add(sub);

   clockLabel=LabelText("",10,Muted,false);clockLabel.Dock=DockStyle.Right;clockLabel.Width=190;clockLabel.TextAlign=ContentAlignment.MiddleRight;header.Controls.Add(clockLabel);

   accountButton=ButtonText("계정 로그인",true);accountButton.Size=new Size(118,34);accountButton.Anchor=AnchorStyles.Top|AnchorStyles.Right;accountButton.Left=header.Width-350;accountButton.Top=17;

   accountStatus=LabelText("로그인 전",8,Muted,false);accountStatus.Size=new Size(190,22);accountStatus.Top=55;accountStatus.Anchor=AnchorStyles.Top|AnchorStyles.Right;

   header.SizeChanged+=delegate{accountButton.Left=header.Width-350;accountStatus.Left=header.Width-350;};

   accountButton.Click+=async delegate{await ManageAccountAsync();};header.Controls.Add(accountButton);header.Controls.Add(accountStatus);

   shell.Controls.Add(header,0,0);

   TableLayoutPanel content=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Padding=new Padding(18,18,18,14),Margin=new Padding(0)};

   content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,230));content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,244));shell.Controls.Add(content,0,1);

   Panel left=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(15),Margin=new Padding(0,0,14,0)};content.Controls.Add(left,0,0);

   TableLayoutPanel leftStack=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=8,ColumnCount=1,Margin=new Padding(0)};

   leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute,34));leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute,29));leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute,47));leftStack.RowStyles.Add(new RowStyle(SizeType.Percent,100));leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute,44));leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute,44));leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute,44));leftStack.RowStyles.Add(new RowStyle(SizeType.Absolute,68));left.Controls.Add(leftStack);

   Label title=LabelText("카메라",14,Ink,true);title.Dock=DockStyle.Fill;leftStack.Controls.Add(title,0,0);

   countLabel=LabelText("등록된 카메라 0",9,Muted,false);countLabel.Dock=DockStyle.Fill;leftStack.Controls.Add(countLabel,0,1);

   Button add=ButtonText("+  카메라 추가",true);addButton=add;add.Dock=DockStyle.Fill;add.Margin=new Padding(0,2,0,9);add.Click+=delegate{EditCamera(null);};leftStack.Controls.Add(add,0,2);

   cameraList=new ListBox{Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=59,IntegralHeight=false,BackColor=Color.White,Margin=new Padding(0,0,0,8)};

   cameraList.DrawItem+=DrawCameraItem;cameraList.SelectedIndexChanged+=async delegate{if(!refreshing){await StopPtzAsync();selected=cameraList.SelectedItem as CameraProfile;EnsureSelectedVisible();UpdateSelection();}};cameraList.DoubleClick+=delegate{ConnectSelected();};leftStack.Controls.Add(cameraList,0,3);

   TableLayoutPanel editRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new Padding(0)};editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));

   editButton=ButtonText("편집",false);editButton.Dock=DockStyle.Fill;editButton.Margin=new Padding(0,0,3,7);editButton.Click+=delegate{EditCamera(selected);};editRow.Controls.Add(editButton,0,0);

   removeButton=ButtonText("삭제",false);removeButton.Dock=DockStyle.Fill;removeButton.Margin=new Padding(3,0,0,7);removeButton.Click+=async delegate{await DeleteSelected();};editRow.Controls.Add(removeButton,1,0);leftStack.Controls.Add(editRow,0,4);

   Button allConnect=ButtonText("모든 카메라 연결",false);allConnect.Dock=DockStyle.Fill;allConnect.Margin=new Padding(0,0,0,7);allConnect.Click+=async delegate{await ConnectAllAsync();};leftStack.Controls.Add(allConnect,0,5);

   Button allStop=ButtonText("모든 영상 연결 해제",false);allStop.Dock=DockStyle.Fill;allStop.Margin=new Padding(0,0,0,7);allStop.Click+=async delegate{CancelRequestedConnections();await StopPtzAsync();StopAllVideos();};leftStack.Controls.Add(allStop,0,6);

   Label help=LabelText("계정 로그인하면 앱에 등록한 카메라를 불러옵니다.\n직접 주소를 등록하는 연결도 가능합니다.",9,Muted,false);cameraHelp=help;help.Dock=DockStyle.Fill;leftStack.Controls.Add(help,0,7);

   TableLayoutPanel center=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Margin=new Padding(0,0,14,0)};

   center.RowStyles.Add(new RowStyle(SizeType.Absolute,46));center.RowStyles.Add(new RowStyle(SizeType.Percent,100));center.RowStyles.Add(new RowStyle(SizeType.Absolute,54));content.Controls.Add(center,1,0);

   Panel toolbar=new Panel{Dock=DockStyle.Fill,Margin=new Padding(0)};

   Label liveTitle=LabelText("실시간 모니터",13,Ink,true);liveTitle.Location=new Point(0,0);liveTitle.Size=new Size(210,35);toolbar.Controls.Add(liveTitle);

   layoutBox=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=102,Anchor=AnchorStyles.Top|AnchorStyles.Right};layoutBox.Items.AddRange(new object[]{"4 분할","9 분할","12 분할"});layoutBox.SelectedIndex=0;layoutBox.Location=new Point(Math.Max(210,toolbar.Width-102),3);toolbar.SizeChanged+=delegate{layoutBox.Left=toolbar.Width-layoutBox.Width;};

   layoutBox.SelectedIndexChanged+=async delegate{await StopPtzAsync();layoutCount=new[]{4,9,12}[layoutBox.SelectedIndex];page=0;BuildGrid();};toolbar.Controls.Add(layoutBox);center.Controls.Add(toolbar,0,0);

   grid=new TableLayoutPanel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(225,231,239),Padding=new Padding(0),Margin=new Padding(0)};center.Controls.Add(grid,0,1);

   Panel pager=new Panel{Dock=DockStyle.Fill,Margin=new Padding(0)};

   previousPage=ButtonText("‹",false);previousPage.Size=new Size(34,30);previousPage.Location=new Point(0,12);previousPage.Click+=async delegate{await StopPtzAsync();page--;BuildGrid();};pager.Controls.Add(previousPage);

   pageLabel=LabelText("1 / 1",9,Muted,false);pageLabel.Location=new Point(43,12);pageLabel.Size=new Size(100,30);pager.Controls.Add(pageLabel);

   nextPage=ButtonText("›",false);nextPage.Size=new Size(34,30);nextPage.Location=new Point(130,12);nextPage.Click+=async delegate{await StopPtzAsync();page++;BuildGrid();};pager.Controls.Add(nextPage);

   Label pageNote=LabelText("페이지를 바꿔도 모든 카메라 연결이 유지됩니다.",8,Muted,false);pageNote.Dock=DockStyle.Right;pageNote.Width=290;pager.Controls.Add(pageNote);center.Controls.Add(pager,0,2);

   Panel right=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(18),Margin=new Padding(0),AutoScroll=true};content.Controls.Add(right,2,0);

   TableLayoutPanel rightStack=new TableLayoutPanel{Dock=DockStyle.Top,Height=650,ColumnCount=1,RowCount=15,Margin=new Padding(0)};

   int[] heights={28,28,32,42,42,18,34,160,70,26,60,32,42,30};foreach(int h in heights)rightStack.RowStyles.Add(new RowStyle(SizeType.Absolute,h));rightStack.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.Controls.Add(rightStack);

   Label rtitle=LabelText("선택한 카메라",12,Ink,true);rtitle.Dock=DockStyle.Fill;rightStack.Controls.Add(rtitle,0,0);

   selectedName=LabelText("카메라를 선택하세요",11,Ink,true);selectedName.Dock=DockStyle.Fill;selectedName.AutoEllipsis=true;rightStack.Controls.Add(selectedName,0,1);

   selectedAddress=LabelText("등록 후 영상을 연결할 수 있습니다.",9,Muted,false);selectedAddress.Dock=DockStyle.Fill;rightStack.Controls.Add(selectedAddress,0,2);

   connectButton=ButtonText("영상 연결",true);connectButton.Dock=DockStyle.Fill;connectButton.Margin=new Padding(0,0,0,10);connectButton.Click+=delegate{ConnectSelected();};rightStack.Controls.Add(connectButton,0,3);

   disconnectButton=ButtonText("연결 해제",false);disconnectButton.Dock=DockStyle.Fill;disconnectButton.Margin=new Padding(0,0,0,10);disconnectButton.Click+=async delegate{VideoTile tile=FindTile(selected);if(tile!=null)tile.CancelReconnect();await StopPtzAsync();if(tile!=null)tile.Disconnect();};rightStack.Controls.Add(disconnectButton,0,4);

   Panel line=new Panel{Dock=DockStyle.Top,Height=1,BackColor=Border,Margin=new Padding(0,12,0,0)};rightStack.Controls.Add(line,0,5);

   Label ptitle=LabelText("카메라 방향  ·  PTZ",12,Ink,true);ptitle.Dock=DockStyle.Fill;rightStack.Controls.Add(ptitle,0,6);

   TableLayoutPanel pad=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=3,Margin=new Padding(8,0,8,8)};

   for(int i=0;i<3;i++){pad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333F));pad.RowStyles.Add(new RowStyle(SizeType.Percent,33.333F));}

   AddDirection(pad,"▲",PtzDirection.Up,1,0);AddDirection(pad,"◀",PtzDirection.Left,0,1);AddDirection(pad,"▶",PtzDirection.Right,2,1);AddDirection(pad,"▼",PtzDirection.Down,1,2);

   stopButton=ButtonText("■",true);stopButton.Dock=DockStyle.Fill;stopButton.Margin=new Padding(3);stopButton.Click+=async delegate{await StopPtzAsync(true);};pad.Controls.Add(stopButton,1,1);rightStack.Controls.Add(pad,0,7);

   Label holdNote=LabelText(ptz.IsReady?"방향 버튼을 누르는 동안 계속 이동합니다.\n버튼을 놓거나 정지 버튼을 누르면 멈춥니다.":"PTZ 전송 모듈 연결 후 사용할 수 있습니다.\n현재 방향 제어는 비활성입니다.",9,Muted,false);holdNote.Dock=DockStyle.Fill;rightStack.Controls.Add(holdNote,0,8);

   Label stateCaption=LabelText("PTZ 연결 상태",9,Muted,true);stateCaption.Dock=DockStyle.Fill;rightStack.Controls.Add(stateCaption,0,9);

   ptzStatus=LabelText(this.ptz.Status,9,Ink,false);ptzStatus.Dock=DockStyle.Fill;rightStack.Controls.Add(ptzStatus,0,10);

   audioEnabled=new CheckBox{Text="선택한 카메라 소리",Checked=true,Dock=DockStyle.Fill,AutoSize=false,Margin=new Padding(0)};

   audioEnabled.CheckedChanged+=delegate{ApplyAudioSelection();};rightStack.Controls.Add(audioEnabled,0,11);

   TableLayoutPanel volumeRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Margin=new Padding(0)};

   volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,40));

   audioVolume=new TrackBar{Minimum=0,Maximum=100,Value=70,TickStyle=TickStyle.None,SmallChange=5,LargeChange=10,Dock=DockStyle.Fill,Margin=new Padding(0)};

   audioVolumeLabel=LabelText("70%",9,Muted,false);audioVolumeLabel.Dock=DockStyle.Fill;

   audioVolume.ValueChanged+=delegate{audioVolumeLabel.Text=audioVolume.Value+"%";ApplyAudioSelection();};

   volumeRow.Controls.Add(audioVolume,0,0);volumeRow.Controls.Add(audioVolumeLabel,1,0);rightStack.Controls.Add(volumeRow,0,12);

   Label audioNote=LabelText("선택한 화면의 소리를 재생합니다.",8,Muted,false);audioNote.Dock=DockStyle.Fill;rightStack.Controls.Add(audioNote,0,13);

   banner=LabelText("준비 · 카메라를 추가하면 이 화면에서 영상을 볼 수 있습니다.",9,Muted,false);banner.Dock=DockStyle.Fill;banner.Padding=new Padding(26,0,20,0);banner.BackColor=Color.White;shell.Controls.Add(banner,0,2);

  }

  private void AddDirection(TableLayoutPanel pad,string text,PtzDirection direction,int col,int row)

  {

   Button b=ButtonText(text,false);b.Dock=DockStyle.Fill;b.Margin=new Padding(3);b.Font=new Font("Segoe UI Symbol",16);b.Tag=direction;

   b.MouseDown+=async delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)await StartPtzAsync(direction,b,Keys.None);};

   b.MouseUp+=async delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left&&heldPtzButton==b)await StopPtzAsync();};

   b.MouseCaptureChanged+=async delegate{if(heldPtzButton==b&&!b.Capture)await StopPtzAsync();};

   directionButtons.Add(b);pad.Controls.Add(b,col,row);

  }

  private void DrawCameraItem(object sender,DrawItemEventArgs e)

  {

   if(e.Index<0)return;CameraProfile p=(CameraProfile)cameraList.Items[e.Index];

   bool isSelected=(e.State&DrawItemState.Selected)!=0;using(SolidBrush brush=new SolidBrush(isSelected?Color.FromArgb(231,239,255):Color.White))e.Graphics.FillRectangle(brush,e.Bounds);

   if(isSelected)using(SolidBrush brush=new SolidBrush(Accent))e.Graphics.FillRectangle(brush,e.Bounds.Left,e.Bounds.Top+7,3,e.Bounds.Height-14);

   Rectangle text=new Rectangle(e.Bounds.Left+12,e.Bounds.Top+8,e.Bounds.Width-20,22);using(Font boldFont=new Font(Font,FontStyle.Bold))TextRenderer.DrawText(e.Graphics,p.Name,boldFont,text,Ink,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);

   text.Y+=24;text.Height=18;TextRenderer.DrawText(e.Graphics,p.AccountCamera?"계정 카메라":p.DisplayAddress,Font,text,Muted,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);

  }

  private void RefreshCameraList()

  {

   refreshing=true;string id=selected==null?null:selected.Id;cameraList.Items.Clear();foreach(CameraProfile c in cameras)cameraList.Items.Add(c);

   selected=cameras.FirstOrDefault(c=>c.Id==id)??cameras.FirstOrDefault();cameraList.SelectedItem=selected;refreshing=false;

   countLabel.Text="등록된 카메라 "+cameras.Count.ToString();UpdateSelection();

  }

  private void BuildGrid()
  {
   grid.SuspendLayout();
   // Keep live renderer controls (and their HWNDs) when changing pages or layout.
   foreach(VideoTile tile in tiles.ToArray()){
    if(tile.Camera==null||!cameras.Contains(tile.Camera)){tiles.Remove(tile);tile.Dispose();}
    else inactiveVideoHost.Controls.Add(tile);
   }
   grid.Controls.Clear();
   foreach(CameraProfile camera in cameras)if(FindTile(camera)==null){
    VideoTile tile=new VideoTile(cameras.IndexOf(camera)+1,camera,utcNow){Dock=DockStyle.Fill,Margin=new Padding(3)};
    tile.Selected+=delegate(object sender,EventArgs e){SelectCamera(((VideoTile)sender).Camera);};
    tile.ConnectRequested+=delegate(object sender,EventArgs e){ConnectTile((VideoTile)sender);};
    tiles.Add(tile);inactiveVideoHost.Controls.Add(tile);
   }
   int columns=layoutCount==4?2:layoutCount==9?3:4,rows=layoutCount==4?2:3;
   grid.ColumnCount=columns;grid.RowCount=rows;grid.ColumnStyles.Clear();grid.RowStyles.Clear();
   for(int i=0;i<columns;i++)grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/columns));
   for(int i=0;i<rows;i++)grid.RowStyles.Add(new RowStyle(SizeType.Percent,100F/rows));
   int pages=Math.Max(1,(cameras.Count+layoutCount-1)/layoutCount);page=Math.Max(0,Math.Min(page,pages-1));
   for(int i=0;i<layoutCount;i++){
    int index=page*layoutCount+i;VideoTile tile;
    if(index<cameras.Count)tile=FindTile(cameras[index]);
    else{tile=new VideoTile(index+1,null,utcNow){Dock=DockStyle.Fill,Margin=new Padding(3)};tiles.Add(tile);}
    grid.Controls.Add(tile,i%columns,i/columns);
   }
   if(selected!=null&&(cameras.IndexOf(selected)<page*layoutCount||cameras.IndexOf(selected)>=(page+1)*layoutCount)){
    selected=cameras.Skip(page*layoutCount).FirstOrDefault();refreshing=true;cameraList.SelectedItem=selected;refreshing=false;
   }
   pageLabel.Text=(page+1).ToString()+" / "+pages.ToString();previousPage.Enabled=page>0;nextPage.Enabled=page+1<pages;
   grid.ResumeLayout();UpdateSelection();
  }

  private void EnsureSelectedVisible()

  {if(selected==null)return;int wanted=cameras.IndexOf(selected)/layoutCount;if(wanted!=page){page=wanted;BuildGrid();}}

  private async void SelectCamera(CameraProfile camera)

  {

   if(camera==null||camera==selected)return;await StopPtzAsync();selected=camera;refreshing=true;cameraList.SelectedItem=camera;refreshing=false;UpdateSelection();

  }

  private void UpdateSelection()

  {

   if(selectedName==null)return;bool has=selected!=null;

   selectedName.Text=has?selected.Name:"카메라를 선택하세요";selectedAddress.Text=has?selected.DisplayAddress:"등록 후 영상을 연결할 수 있습니다.";

   editButton.Enabled=removeButton.Enabled=has&&!accountMode&&!closing&&!accountBusy;

   addButton.Enabled=!accountMode&&!closing&&!accountBusy;connectButton.Enabled=disconnectButton.Enabled=has&&!closing;

   foreach(Button b in directionButtons)b.Enabled=has&&selected.PtzEnabled&&ptz.IsReady&&!closing&&!accountBusy;stopButton.Enabled=has&&selected.PtzEnabled&&ptz.IsReady&&!closing&&!accountBusy;

   foreach(VideoTile tile in tiles)tile.IsSelected=has&&tile.Camera!=null&&tile.Camera.Id==selected.Id;

   IAccountPtzController account=ptz as IAccountPtzController;

   if(accountButton!=null){accountButton.Enabled=account!=null&&!closing&&!accountBusy;accountButton.Text=account!=null&&account.IsLoggedIn?"로그아웃":"계정 로그인";accountStatus.Text=account!=null&&account.IsLoggedIn?(ptz.IsReady?"계정 영상 · 연결 준비됨":"영상 모듈 연결 끊김 · 재로그인"):"계정 로그인으로 영상 보기";}

   ApplyAudioSelection();

  }

  private void RecordAccountStatus(string message){

   // Versioned status cannot be overwritten by an older running build. Fixed codes and structural metadata only.

   try{string folder=Path.GetDirectoryName(store.FilePath);Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"account-status-1.4.2.txt"),DateTime.UtcNow.ToString("o")+" version=1.4.2 "+message);}

   catch(IOException){}catch(UnauthorizedAccessException){}

  }

  private async Task LoginAccountAsync(string username,string password,CancellationToken token){

   RecordAccountStatus("LOGIN_STARTED");

   try{await ((IAccountPtzController)ptz).LoginAsync(username,password,token);RecordAccountStatus("LOGIN_OK");}

   catch(CloudAccountException ex){RecordAccountStatus("LOGIN_ERROR "+ex.Code+(String.IsNullOrEmpty(ex.SafeDetail)?"":" "+ex.SafeDetail));throw;}

   catch(OperationCanceledException){RecordAccountStatus("LOGIN_CANCELLED");throw;}

   catch(NativePtzException ex){RecordAccountStatus("SDK_NATIVE_"+ex.Code.ToString(System.Globalization.CultureInfo.InvariantCulture));throw;}

   catch(Exception){RecordAccountStatus("SDK_REGISTER_ERROR");throw;}

  }

  public Task OpenAccountDialogAsync(){return ManageAccountAsync();}

  public async Task StartAccountAsync(bool forceManual){
   if(startupHandled)return;startupHandled=true;
   if(forceManual)await ManageAccountCoreAsync(false);
   else if(autoLoginStore.HasSavedCredentials)await ManageAccountCoreAsync(true);
   else if(settingsLoaded)await ConnectAllAsync();
  }

  private Task ManageAccountAsync(){return ManageAccountCoreAsync(false);}

  private async Task ManageAccountCoreAsync(bool automatic){

   IAccountPtzController account=ptz as IAccountPtzController;if(account==null||accountBusy||closing)return;

   accountBusy=true;await StopPtzAsync();UpdateSelection();string clearWarning=null;

   try{

    if(account.IsLoggedIn){

     autoLoginStore.ClearSaved(out clearWarning);

     StopAllVideos();try{using(CancellationTokenSource timeout=new CancellationTokenSource(10000))await account.LogoutAsync(timeout.Token);}

     finally{if(!account.IsLoggedIn)RestoreLocalCameras();}

     ShowNotice(clearWarning??"로그아웃했습니다. 자동로그인도 해제되었습니다.",clearWarning!=null);

    }else using(AccountLoginForm dialog=new AccountLoginForm(LoginAccountAsync,autoLoginStore,automatic)){

     if(dialog.ShowDialog(this)==DialogResult.OK&&!closing){

      IAccountVideoController video=ptz as IAccountVideoController;

      if(video==null)throw new InvalidOperationException();

      LoadAccountCameras(video.AccountDevices);

      RecordAccountStatus("ACCOUNT_LIST count="+cameras.Count);

      if(!String.IsNullOrEmpty(video.DeviceListErrorCode))ShowNotice("로그인은 완료됐지만 카메라 목록을 불러오지 못했습니다. 로그아웃 후 다시 시도해 주세요.",true);

      else if(cameras.Count==0)ShowNotice("계정에 등록된 카메라가 없습니다. 휴대폰 앱에서 카메라 등록을 확인해 주세요.",true);

      else{ShowNotice("계정 로그인 완료 · 모든 카메라 영상을 연결하고 있습니다…",false);await ConnectAllAsync();}

      if(dialog.PersistenceWarning!=null)ShowNotice(dialog.PersistenceWarning,true);

     }

    }

   }catch(Exception){ShowNotice(clearWarning??"계정 연결을 변경하지 못했습니다. 연결 상태를 확인해 주세요.",true);}

   finally{accountBusy=false;if(!closing&&!IsDisposed)UpdateSelection();}

  }

  internal void LoadAccountCameras(IEnumerable<CloudDeviceInfo> devices){

   StopAllVideos();cameras.Clear();HashSet<ulong> seen=new HashSet<ulong>();

   foreach(CloudDeviceInfo device in devices){

    ulong id;if(device==null||!UInt64.TryParse(device.DeviceId,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out id)||id==0||!seen.Add(id))continue;

    cameras.Add(new CameraProfile{AccountCamera=true,DeviceId=id.ToString(System.Globalization.CultureInfo.InvariantCulture),Name=String.IsNullOrWhiteSpace(device.Name)?"계정 카메라 "+(cameras.Count+1):device.Name,DevConfig=device.DevConfig,AccountProtocol=device.PtzProtocol,PtzEnabled=true,Host="",Username="",ProtectedPassword=""});

   }

   accountMode=true;selected=null;page=0;cameraHelp.Text="휴대폰 앱에 등록된 계정 카메라입니다.\n목록 변경은 휴대폰 앱에서 해 주세요.";RefreshCameraList();BuildGrid();

  }

  internal void RestoreLocalCameras(){

   StopAllVideos();string warning;var saved=store.Load(out warning);settingsLoaded=warning==null;cameras.Clear();cameras.AddRange(saved);accountMode=false;selected=null;page=0;

   cameraHelp.Text="계정 로그인하면 앱에 등록한 카메라를 불러옵니다.\n직접 주소를 등록하는 연결도 가능합니다.";RefreshCameraList();BuildGrid();if(warning!=null)ShowNotice(warning,true);

  }

  private void ApplyAudioSelection()

  {

   if(audioEnabled==null||audioVolume==null)return;

   audioEnabled.Enabled=selected!=null&&!closing;audioVolume.Enabled=audioEnabled.Checked&&selected!=null&&!closing;

   foreach(VideoTile tile in tiles)tile.SetAudio(audioEnabled.Checked&&tile.IsSelected&&tile.Parent==grid,audioVolume.Value);

  }

  private void InitializeEngine()

  {

   if(initializedEngine)return;initializedEngine=true;

   try{engine=new VlcEngine();if(settingsLoaded)ShowNotice("준비 · 영상 엔진 연결됨. 카메라를 등록하고 영상 연결을 눌러 주세요.",false);}

   catch(Exception ex){if(!(ex is InvalidOperationException)&&!(ex is DllNotFoundException)&&!(ex is BadImageFormatException)&&!(ex is EntryPointNotFoundException))throw;ShowNotice("영상 엔진을 사용할 수 없습니다. 공식 ipTIME CAM Multi Viewer 설치를 확인해 주세요.",true);}

  }

  private VideoTile FindTile(CameraProfile camera){return camera==null?null:tiles.FirstOrDefault(t=>t.Camera!=null&&t.Camera.Id==camera.Id);}

  private void ConnectSelected(){EnsureSelectedVisible();VideoTile tile=FindTile(selected);if(tile!=null)ConnectTile(tile);}

  private bool ConnectTile(VideoTile tile){tile.RequestConnection();return StartTileConnection(tile);}

  private bool StartTileConnection(VideoTile tile)

  {

   if(tile.Camera==null||closing||tile.IsDisposed||!tile.WantsConnection)return false;

   try{

    if(tile.Camera.AccountCamera){

     var account=ptz as IAccountPtzController;var video=ptz as IAccountVideoController;

     if(account==null||!account.IsLoggedIn||video==null){tile.Disconnect();ShowNotice("먼저 계정으로 로그인해 주세요.",true);return false;}

     tile.ConnectAccount(video);

    }else{tile.ConnectLocal(localVideoFactory);}

    ApplyAudioSelection();ShowNotice(tile.Camera.Name+" · 영상 연결을 시도하고 있습니다.",false);return true;

   }catch(Exception){tile.ConnectionFailed("연결 시작 실패");ShowNotice(tile.Camera.Name+" · 연결 실패, 자동으로 재시도합니다.",true);return false;}

  }

  private IVideoPlayer CreateLocalVideoPlayer(CameraProfile camera,IntPtr window){
   InitializeEngine();if(engine==null)throw new InvalidOperationException("Video engine unavailable");
   VlcPlayer player=engine.CreatePlayer(window);
   try{player.Play(camera);return player;}catch{player.Dispose();throw;}
  }

  private void CancelConnectionBatch(){
   CancellationTokenSource pending=connectionBatch;connectionBatch=null;if(pending!=null)pending.Cancel();
  }

  private async Task ConnectAllAsync(){
   if(closing)return;CancelConnectionBatch();
   var pending=new CancellationTokenSource();connectionBatch=pending;
   int started=0,failed=0,active=0;
   try{
    VideoTile[] snapshot=tiles.Where(t=>t.Camera!=null).ToArray();
    foreach(VideoTile tile in snapshot)if(!tile.HasConnection)tile.RequestConnection();
    for(int i=0;i<snapshot.Length;i++){
     pending.Token.ThrowIfCancellationRequested();if(closing)return;
     VideoTile tile=snapshot[i];if(tile.IsDisposed||!tiles.Contains(tile)||!tile.WantsConnection)continue;
     tile.RefreshStatus();
     if(tile.HasConnection)active++;
     else if(StartTileConnection(tile))started++;else failed++;
     if(i+1<snapshot.Length)await Task.Delay(100,pending.Token);
    }
    ShowNotice("전체 카메라 · 연결 중/유지 "+(started+active)+"대"+(failed>0?" · 연결 시작 실패 "+failed+"대 (자동 재시도 예정)":""),failed>0);
   }catch(OperationCanceledException){}
   finally{if(ReferenceEquals(connectionBatch,pending))connectionBatch=null;pending.Dispose();}
  }

  private async Task ReconnectDueAsync(){
   if(reconnectBusy||closing||accountBusy||connectionBatch!=null)return;
   VideoTile tile=tiles.FirstOrDefault(t=>!t.IsDisposed&&t.RetryDue);
   if(tile==null)return;reconnectBusy=true;
   try{
    if(movingCamera==tile.Camera)await StopPtzAsync();else if(!stoppingTask.IsCompleted)await stoppingTask;
    if(closing||accountBusy||connectionBatch!=null||tile.IsDisposed||!tiles.Contains(tile)||!tile.RetryDue)return;
    StartTileConnection(tile);
   }finally{reconnectBusy=false;}
  }

  private void CancelRequestedConnections(){CancelConnectionBatch();foreach(VideoTile tile in tiles)tile.CancelReconnect();}

  private void StopAllVideos(){CancelRequestedConnections();foreach(VideoTile tile in tiles)tile.Disconnect();}

  private async void EditCamera(CameraProfile camera)

  {

   if(accountMode||accountBusy)return;

   using(CameraEditor editor=new CameraEditor(camera)){

    if(editor.ShowDialog(this)!=DialogResult.OK)return;

    CameraProfile updated=editor.Result;List<CameraProfile> candidate=new List<CameraProfile>(cameras);

    int index=camera==null?-1:candidate.IndexOf(camera);if(index>=0)candidate[index]=updated;else candidate.Add(updated);

    if(!Persist(candidate))return;

    await StopPtzAsync();cameras.Clear();cameras.AddRange(candidate);selected=updated;RefreshCameraList();page=cameras.IndexOf(selected)/layoutCount;BuildGrid();ShowNotice("카메라 정보를 저장했습니다.",false);

   }

  }

  private async Task DeleteSelected()

  {

   if(selected==null||accountMode||accountBusy)return;CameraProfile camera=selected;

   if(MessageBox.Show(this,"‘"+camera.Name+"’ 카메라를 목록에서 삭제할까요?","카메라 삭제",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;

   List<CameraProfile> candidate=new List<CameraProfile>(cameras);candidate.Remove(camera);if(!Persist(candidate))return;

   await StopPtzAsync();cameras.Clear();cameras.AddRange(candidate);selected=null;RefreshCameraList();BuildGrid();ShowNotice("카메라를 목록에서 삭제했습니다.",false);

  }

  private bool Persist(List<CameraProfile> candidate)

  {

   if(accountMode||candidate.Any(c=>c.AccountCamera))return false;

   if(!settingsLoaded){ShowNotice("기존 설정 파일 읽기 오류가 있어 저장을 중지했습니다. 설정 파일을 확인한 뒤 앱을 다시 열어 주세요.",true);return false;}

   try{store.Save(candidate);return true;}catch(Exception){ShowNotice("카메라 설정을 저장하지 못했습니다. 저장 폴더 권한을 확인해 주세요.",true);return false;}

  }

  private void RefreshState()

  {

   clockLabel.Text=DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");foreach(VideoTile t in tiles)t.RefreshStatus();

   ptzStatus.Text=ptz.Status;UpdateSelection();

  }

  private void ShowNotice(string text,bool error){if(IsDisposed||closing)return;banner.Text=text;banner.ForeColor=error?Color.FromArgb(170,68,40):Muted;}

  private async Task StartPtzAsync(PtzDirection direction,Button sourceButton,Keys sourceKey)

  {

   if(selected==null||!selected.PtzEnabled||!ptz.IsReady||closing||accountBusy||movingCamera!=null||!stoppingTask.IsCompleted)return;

   CameraProfile camera=selected;movingCamera=camera;long current=++gesture;

   heldPtzButton=sourceButton;heldPtzKey=sourceKey;

   motionTrace.Clear();motionClock.Restart();TraceMotion("BEGIN "+direction+" "+(sourceButton==null?"keyboard":"mouse"));

   CancellationTokenSource cancellation=new CancellationTokenSource();motionCancellation=cancellation;

   CancellationToken token=cancellation.Token;

   try{

    bool failed=false;

    try{

    // Keep a bounded start-to-start cadence instead of adding a long gap after

    // each response. There is only one in-flight command; release cancels it.

    while(gesture==current&&!token.IsCancellationRequested){

     System.Diagnostics.Stopwatch cycle=System.Diagnostics.Stopwatch.StartNew();

     await ptzGate.WaitAsync(token);

     try{

      if(gesture!=current||token.IsCancellationRequested)return;

      TraceMotion("SEND "+direction);

      await ptz.MoveAsync(camera,direction,token);

      TraceMotion("REQUEST_COMPLETE "+direction+" elapsed_ms="+cycle.ElapsedMilliseconds);

      var protocol=ptz as IHoldPtzController;if(protocol!=null)TraceMotion(protocol.RepeatWhileHeld?"PROTOCOL legacy_repeat":"PROTOCOL touch_hold");

     }finally{ptzGate.Release();}

     IHoldPtzController held=ptz as IHoldPtzController;

     if(held!=null&&!held.RepeatWhileHeld){await Task.Delay(Timeout.Infinite,token);break;}

     int repeatMilliseconds=held==null?PtzRefreshMilliseconds:Math.Max(50,held.RepeatIntervalMilliseconds);

     await Task.Delay(Math.Max(10,repeatMilliseconds-(int)Math.Min(cycle.ElapsedMilliseconds,Int32.MaxValue)),token);

    }

    }catch(OperationCanceledException){TraceMotion("CANCELLED");failed=true;}

    catch(Exception){

     TraceMotion("MOVEMENT_ERROR");

     if(gesture==current)ShowNotice("PTZ 명령을 완료하지 못했습니다. 장치 연결 상태를 확인해 주세요.",true);

     failed=true;

    }

    if(failed&&gesture==current)await StopPtzAsync();

   }finally{

    // The hold loop owns disposal; a release may cancel while a request is pending.

    if(ReferenceEquals(motionCancellation,cancellation))motionCancellation=null;

    cancellation.Dispose();

   }

  }

  private Task StopPtzAsync(bool force=false)

  {

   CameraProfile camera=movingCamera??(force?selected:null);

   if(camera==null)return stoppingTask;

   TraceMotion("STOP_REQUEST");

   movingCamera=null;++gesture;

   Button button=heldPtzButton;heldPtzButton=null;heldPtzKey=Keys.None;

   CancellationTokenSource cancellation=motionCancellation;motionCancellation=null;

   // Register the stop before cancellation/capture callbacks can reenter this form.

   TaskCompletionSource<bool> completion=new TaskCompletionSource<bool>();

   Task previous=stoppingTask;stoppingTask=completion.Task;

   if(cancellation!=null)cancellation.Cancel();

   if(button!=null&&button.Capture)button.Capture=false;

   CompleteStopPtzAsync(camera,previous,completion);

   return stoppingTask;

  }

  private async void CompleteStopPtzAsync(CameraProfile camera,Task previous,TaskCompletionSource<bool> completion)

  {

   try{

    await previous;

    await ptzGate.WaitAsync();

    try{using(CancellationTokenSource stopTimeout=new CancellationTokenSource(3000))await ptz.MoveAsync(camera,PtzDirection.Stop,stopTimeout.Token);TraceMotion("STOP_HANDLED");}

    finally{ptzGate.Release();}

   }catch(Exception){if(!closing)ShowNotice("PTZ 정지 응답을 확인하지 못했습니다. 카메라 상태를 확인해 주세요.",true);}

   finally{SaveMotionTrace();completion.TrySetResult(true);}

  }

  private void TraceMotion(string text)

  {

   if(motionTrace.Count>=2048)motionTrace.RemoveAt(0);

   motionTrace.Add(motionClock.ElapsedMilliseconds.ToString()+" "+text);

  }

  private void SaveMotionTrace()

  {

   // Timing and direction only: never include camera addresses, credentials or video.

   try{File.WriteAllLines(Path.Combine(Path.GetDirectoryName(store.FilePath),"PTZ-last-session-1.4.2.txt"),motionTrace);}

   catch(IOException){}catch(UnauthorizedAccessException){}

  }

  private bool IsArrow(Keys key){return key==Keys.Up||key==Keys.Down||key==Keys.Left||key==Keys.Right;}

  private async void OnViewerKeyDown(object sender,KeyEventArgs e)

  {

   if(!IsArrow(e.KeyCode)||e.Alt||e.Control||ActiveControl is TextBoxBase||ActiveControl is ComboBox||ActiveControl is ListBox||ActiveControl is TrackBar||ActiveControl==accountButton)return;

   PtzDirection d=e.KeyCode==Keys.Up?PtzDirection.Up:e.KeyCode==Keys.Down?PtzDirection.Down:e.KeyCode==Keys.Left?PtzDirection.Left:PtzDirection.Right;

   e.Handled=true;await StartPtzAsync(d,null,e.KeyCode);

  }

  private async void OnFormClosing(object sender,FormClosingEventArgs e)

  {

   if(finalClose)return;e.Cancel=true;if(closing)return;closing=true;timer.Stop();UpdateSelection();

   await StopPtzAsync();StopAllVideos();foreach(VideoTile tile in tiles)tile.Dispose();tiles.Clear();

   if(engine!=null){engine.Dispose();engine=null;}await Task.Run(()=>ptz.Dispose());Application.RemoveMessageFilter(this);

   timer.Dispose();finalClose=true;Close();

  }

  public bool PreFilterMessage(ref Message m)

  {

   if(m.Msg==0x0201)foreach(VideoTile tile in tiles)if(tile.IsHandleCreated&&(m.HWnd==tile.Handle||IsChild(tile.Handle,m.HWnd))){SelectCamera(tile.Camera);break;}

   return false;

  }

  [DllImport("user32.dll")]private static extern bool IsChild(IntPtr parent,IntPtr child);

  public void SavePreview(string path)

  {using(Bitmap bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(Point.Empty,Size));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);}}

 }

 internal sealed class VideoTile:Panel

 {

  public CameraProfile Camera{get;private set;} public event EventHandler Selected; public event EventHandler ConnectRequested;

  private readonly Panel surface;private readonly Label title,state,empty;private readonly Button connect;

  private IVideoPlayer player;private bool chosen,audioOn;private int volume=70;private DateTime connectStarted;

  private readonly VideoReconnectState retry=new VideoReconnectState();private readonly Func<DateTime> utcNow;private string lastFailure;

  public bool IsSelected{get{return chosen;}set{chosen=value;BackColor=chosen?Color.FromArgb(38,103,235):Color.FromArgb(31,43,60);}}

  public VideoTile(int slot,CameraProfile camera,Func<DateTime> utcNow=null)

  {

   Camera=camera;this.utcNow=utcNow??(()=>DateTime.UtcNow);Padding=new Padding(2);BackColor=Color.FromArgb(31,43,60);

   TableLayoutPanel stack=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=new Padding(0),Padding=new Padding(0)};stack.RowStyles.Add(new RowStyle(SizeType.Absolute,35));stack.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(stack);

   Panel bar=new Panel{Dock=DockStyle.Fill,Margin=new Padding(0),BackColor=Color.FromArgb(24,34,49)};

   title=new Label{Dock=DockStyle.Fill,Text=camera==null?"CH "+slot.ToString("00")+"  /  비어 있음":camera.Name,ForeColor=Color.FromArgb(228,235,246),Font=new Font("맑은 고딕",9,FontStyle.Bold),TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0),AutoEllipsis=true};

   state=new Label{Dock=DockStyle.Right,Width=74,Text=camera==null?"EMPTY":"대기",ForeColor=Color.FromArgb(144,159,182),Font=new Font("맑은 고딕",8),TextAlign=ContentAlignment.MiddleRight,Padding=new Padding(0,0,9,0)};

   bar.Controls.Add(title);bar.Controls.Add(state);stack.Controls.Add(bar,0,0);

   surface=new Panel{Dock=DockStyle.Fill,BackColor=Color.FromArgb(16,24,36),Margin=new Padding(0)};stack.Controls.Add(surface,0,1);

   empty=new Label{AutoSize=false,Text=camera==null?"카메라가 등록되지 않았습니다":"영상을 연결해 주세요",TextAlign=ContentAlignment.MiddleCenter,ForeColor=Color.FromArgb(136,153,179),Font=new Font("맑은 고딕",10),Height=42,Width=260};surface.Controls.Add(empty);

   connect=new Button{Text="연결",FlatStyle=FlatStyle.Flat,ForeColor=Color.FromArgb(206,220,244),BackColor=Color.FromArgb(30,45,66),Size=new Size(78,31),Visible=camera!=null};connect.FlatAppearance.BorderColor=Color.FromArgb(64,86,117);surface.Controls.Add(connect);

   surface.Resize+=delegate{empty.Location=new Point((surface.Width-empty.Width)/2,(surface.Height-80)/2);connect.Location=new Point((surface.Width-connect.Width)/2,empty.Bottom+3);};

   EventHandler select=delegate{if(Selected!=null)Selected(this,EventArgs.Empty);};title.Click+=select;state.Click+=select;surface.Click+=select;empty.Click+=select;

   connect.Click+=delegate{select(this,EventArgs.Empty);if(ConnectRequested!=null)ConnectRequested(this,EventArgs.Empty);};

  }

  public void SetAudio(bool enabled,int level){audioOn=enabled;volume=level;if(player!=null)player.SetAudio(enabled,level);}

  internal bool HasConnection{get{return player!=null;}}
  internal bool WantsConnection{get{return retry.Wanted;}}
  internal bool RetryDue{get{return Camera!=null&&player==null&&retry.IsDue(utcNow());}}
  internal void RequestConnection(){if(Camera!=null)retry.Request();}
  internal void CancelReconnect(){retry.Stop();}

  public void Connect(VlcEngine engine){
   ConnectLocal((camera,window)=>{
    VlcPlayer value=engine.CreatePlayer(window);try{value.Play(camera);return value;}catch{value.Dispose();throw;}
   });
  }
  internal void ConnectLocal(Func<CameraProfile,IntPtr,IVideoPlayer> factory){BeginConnection(factory,"카메라");}
  public void ConnectAccount(IAccountVideoController controller){BeginConnection(controller.CreateVideoPlayer,"계정 카메라");}

  private void BeginConnection(Func<CameraProfile,IntPtr,IVideoPlayer> factory,string kind){
   ReleasePlayer();if(Camera==null)return;retry.BeginAttempt();connectStarted=utcNow();
   player=factory(Camera,surface.Handle);player.SetAudio(audioOn,volume);
   empty.Text=kind+"에 연결 중…";empty.Visible=true;connect.Visible=false;state.Text="연결 중";
  }
  private void ReleasePlayer(){var old=player;player=null;if(old!=null)try{old.Dispose();}catch{}}
  public void Disconnect(){
   retry.Stop();ReleasePlayer();
   if(!IsDisposed){state.Text=Camera==null?"EMPTY":"대기";state.ForeColor=Color.FromArgb(144,159,182);empty.Visible=true;empty.Text=Camera==null?"카메라가 등록되지 않았습니다":"영상을 연결해 주세요";connect.Visible=Camera!=null;}
  }
  internal void ConnectionFailed(string message){
   ReleasePlayer();lastFailure=message;retry.Failed(utcNow());ShowRetryStatus();
  }
  private void ShowRetryStatus(){
   if(IsDisposed)return;
   if(!retry.RetryAt.HasValue){ShowError(lastFailure??"연결 실패");return;}
   int seconds=Math.Max(0,(int)Math.Ceiling((retry.RetryAt.Value-utcNow()).TotalSeconds));
   state.Text="재연결";state.ForeColor=Color.FromArgb(240,155,113);empty.Visible=true;
   empty.Text=(lastFailure??"연결 끊김")+"\n"+(seconds==0?"자동 재연결 대기 중…":seconds+"초 후 자동 재연결");connect.Text="재연결";connect.Visible=Camera!=null;
  }
  public void RefreshStatus(){
   if(player==null){if(retry.RetryAt.HasValue)ShowRetryStatus();return;}
   PlayerState s;
   try{s=player.State;}catch{ConnectionFailed("영상 상태 확인 실패");return;}
   DateTime now=utcNow();
   if(s==PlayerState.Playing){
    retry.Healthy(now);connectStarted=now;player.SetAudio(audioOn,volume);state.Text="● LIVE";state.ForeColor=Color.FromArgb(98,213,164);empty.Visible=false;connect.Visible=false;
   }else{
    retry.NotPlaying();
    if(s==PlayerState.Opening||s==PlayerState.Buffering||s==PlayerState.Idle||s==PlayerState.Paused){
     if((now-connectStarted).TotalSeconds>25)ConnectionFailed("연결 시간 초과");
     else{state.Text=s==PlayerState.Buffering?"버퍼링":"연결 중";empty.Visible=true;empty.Text="카메라에 연결 중…";}
    }else if(s==PlayerState.Error||s==PlayerState.Ended||s==PlayerState.Stopped)ConnectionFailed(s==PlayerState.Error?"영상 연결 실패":"영상 연결 종료");
   }
  }
  public void ShowError(string message){state.Text="오류";state.ForeColor=Color.FromArgb(240,155,113);empty.Visible=true;empty.Text=message;connect.Text="재연결";connect.Visible=Camera!=null;}
  protected override void Dispose(bool disposing){if(disposing){retry.Stop();ReleasePlayer();}base.Dispose(disposing);}

 }

 internal sealed class CameraEditor:Form

 {

  private readonly TextBox nameBox,hostBox,userBox,passwordBox,deviceBox;

  private readonly NumericUpDown portBox;

  private readonly CheckBox ptzEnabledBox;

  private readonly CameraProfile original;

  private readonly bool passwordUnavailable;

  public CameraProfile Result{get;private set;}

  public CameraEditor(CameraProfile camera)

  {

   original=camera==null?new CameraProfile():camera.Copy();Text=camera==null?"카메라 추가":"카메라 편집";

   Font=new Font("맑은 고딕",9);ClientSize=new Size(510,590);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;BackColor=Color.White;

   Label heading=new Label{Text="C500G 연결 정보",Font=new Font(Font.FontFamily,15,FontStyle.Bold),AutoSize=true,Location=new Point(26,22)};Controls.Add(heading);

   nameBox=Input("카메라 이름",original.Name,76);hostBox=Input("IP / 호스트 이름",original.Host,133);userBox=Input("계정",original.Username,190);passwordBox=Input("암호","",247);passwordBox.UseSystemPasswordChar=true;

   try{passwordBox.Text=original.GetPassword();}catch{passwordUnavailable=true;}

   Label portLabel=new Label{Text="RTSP 포트",Location=new Point(27,310),Size=new Size(130,24)};Controls.Add(portLabel);

   portBox=new NumericUpDown{Minimum=1,Maximum=65535,Value=Math.Max(1,Math.Min(65535,original.Port)),Location=new Point(165,308),Size=new Size(115,26)};Controls.Add(portBox);

   Label stream=new Label{Text="HD · /onvif1",ForeColor=Color.FromArgb(105,119,138),Location=new Point(296,311),Size=new Size(170,24)};Controls.Add(stream);

   deviceBox=Input("장치 ID (선택)",original.DeviceId,357);

   ptzEnabledBox=new CheckBox{Text="RTSP PTZ 사용",Checked=original.PtzEnabled,Location=new Point(165,409),Size=new Size(307,28)};Controls.Add(ptzEnabledBox);

   Label note=new Label{Text=passwordUnavailable?"저장된 암호를 복호화할 수 없습니다. 암호를 다시 입력해 주세요.":"PTZ 사용 시 위 RTSP 포트로 방향 제어를 함께 연결합니다.\n암호는 현재 Windows 사용자만 복호화할 수 있게 저장합니다.",ForeColor=Color.FromArgb(105,119,138),Location=new Point(27,456),Size=new Size(450,50)};Controls.Add(note);

   Button cancel=new Button{Text="취소",DialogResult=DialogResult.Cancel,Location=new Point(278,530),Size=new Size(92,34)};Controls.Add(cancel);

   Button save=new Button{Text="저장",BackColor=Color.FromArgb(38,103,235),ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Location=new Point(380,530),Size=new Size(92,34)};save.Click+=Save;Controls.Add(save);AcceptButton=save;CancelButton=cancel;

  }

  private TextBox Input(string label,string value,int y)

  {Controls.Add(new Label{Text=label,Location=new Point(27,y+4),Size=new Size(132,25)});TextBox box=new TextBox{Text=value??"",Location=new Point(165,y),Size=new Size(307,27)};Controls.Add(box);return box;}

  private void Save(object sender,EventArgs e)

  {

   CameraProfile p=original.Copy();p.Name=nameBox.Text.Trim();p.Host=hostBox.Text.Trim();p.Username=userBox.Text;p.DeviceId=deviceBox.Text.Trim();p.Port=(int)portBox.Value;p.StreamPath="/onvif1";p.PtzEnabled=ptzEnabledBox.Checked;

   string validation=p.Validate();if(validation!=null){MessageBox.Show(this,validation,"입력 확인",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}

   if(passwordUnavailable&&String.IsNullOrEmpty(passwordBox.Text)){MessageBox.Show(this,"암호를 다시 입력해 주세요.");return;}

   try{p.SetPassword(passwordBox.Text);}catch{MessageBox.Show(this,"Windows 암호 보호 기능을 사용할 수 없어 저장하지 않았습니다.");return;}

   Result=p;DialogResult=DialogResult.OK;Close();

  }

 }

}

