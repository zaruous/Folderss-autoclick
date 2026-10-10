using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;

namespace AutoClick
{
    /// <summary>한 번 실행할 클릭 내용. 시작할 때 화면 값으로 만들고, 실행 중에는 바꾸지 않는다.</summary>
    internal sealed class ClickJob
    {
        public MouseButtonKind Button;
        public bool DoubleClick;
        public int IntervalMs;

        /// <summary>이만큼 클릭하면 끝낸다. 0이면 멈출 때까지.</summary>
        public int MaxClicks;

        /// <summary>비어 있지 않으면 이 위치들을 차례로 돌아가며 클릭한다.</summary>
        public IReadOnlyList<ScreenPoint> Points = new ScreenPoint[0];

        /// <summary>Points가 비어 있을 때, 클릭을 시작한 순간의 포인터 위치로 매번 옮겨 클릭한다. 아니면 그때그때 포인터가 있는 곳을 클릭한다.</summary>
        public bool FreezePointer;

        /// <summary>이 시각까지 기다렸다가 클릭을 시작한다. null이면 바로.</summary>
        public DateTime? StartAt;

        /// <summary>이 시각이 되면 멈춘다(이 시각 이후로는 클릭하지 않는다). null이면 정하지 않음.</summary>
        public DateTime? StopAt;
    }

    /// <summary>
    /// 백그라운드 스레드에서 클릭을 반복한다. 실행 한 번에 인스턴스 하나를 쓴다.
    /// 이 스레드에서 예외가 빠져나가면 Folderss가 종료되므로 모두 잡아서 끝남 알림으로 넘긴다.
    /// </summary>
    internal sealed class ClickRunner
    {
        /// <summary>시작 시각을 기다릴 때 한 번에 자는 최대 시간. 시계가 바뀌어도(시간 동기화 등) 이 안에 다시 계산한다.</summary>
        private const int MaxWaitSliceMs = 1000;

        /// <summary>시작 시각이 이만큼 남으면 타이머 해상도를 1ms로 올린다. 긴 대기 내내 올려 두면 전력을 더 쓴다.</summary>
        private const double HighResolutionLeadMs = 2000;

        private readonly Dispatcher _dispatcher;
        private readonly Action<ClickRunner, string> _finished;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private int _clicks;
        private int _started;
        private int _reachedStopTime;
        private bool _highResolution;

        /// <param name="finished">끝나면(멈춤·횟수 도달·종료 시각·오류) dispatcher 스레드에서 호출된다. 두 번째 인수는 오류 메시지(정상이면 null).</param>
        public ClickRunner(ClickJob job, Dispatcher dispatcher, Action<ClickRunner, string> finished)
        {
            Job = job;
            _dispatcher = dispatcher;
            _finished = finished;
        }

        public ClickJob Job { get; }

        /// <summary>지금까지 보낸 클릭 수(더블 클릭은 1회). 아무 스레드에서나 읽어도 된다.</summary>
        public int Clicks { get { return Volatile.Read(ref _clicks); } }

        /// <summary>시작 시각 대기를 마치고 클릭을 시작했는지.</summary>
        public bool HasStarted { get { return Volatile.Read(ref _started) != 0; } }

        public bool ReachedLimit { get { return Job.MaxClicks > 0 && Clicks >= Job.MaxClicks; } }

        public bool ReachedStopTime { get { return Volatile.Read(ref _reachedStopTime) != 0; } }

        public void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "AutoClick" };
            thread.Start();
        }

        /// <summary>멈추라고 알리고 바로 돌아온다. 끝남 알림은 스레드가 끝난 뒤에 온다.</summary>
        public void Stop()
        {
            _stop.Set();
        }

        private void Run()
        {
            string error = null;
            try
            {
                if (Job.StartAt.HasValue && WaitUntil(Job.StartAt.Value))
                    return;
                UseHighResolution();
                Volatile.Write(ref _started, 1);
                ClickLoop();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                if (_highResolution)
                    NativeMethods.timeEndPeriod(1);
                NotifyFinished(error);
            }
        }

        private void ClickLoop()
        {
            ScreenPoint? fixedPoint = null;
            if (Job.Points.Count == 0 && Job.FreezePointer)
                fixedPoint = NativeMethods.GetCursorPosition();

            var index = 0;
            while (!_stop.WaitOne(0))
            {
                if (Job.StopAt.HasValue && DateTime.Now >= Job.StopAt.Value)
                {
                    Volatile.Write(ref _reachedStopTime, 1);
                    return;
                }

                var target = fixedPoint;
                if (Job.Points.Count > 0)
                {
                    target = Job.Points[index];
                    index = (index + 1) % Job.Points.Count;
                }
                if (target.HasValue)
                    NativeMethods.MoveCursor(target.Value);
                NativeMethods.Click(Job.Button, Job.DoubleClick);

                var clicks = Interlocked.Increment(ref _clicks);
                if (Job.MaxClicks > 0 && clicks >= Job.MaxClicks)
                    return;

                // 종료 시각이 간격보다 먼저 오면 그때 깨어나서 위에서 멈춘다
                var wait = (double)Job.IntervalMs;
                if (Job.StopAt.HasValue)
                    wait = Math.Min(wait, Math.Max(0, (Job.StopAt.Value - DateTime.Now).TotalMilliseconds));
                if (_stop.WaitOne((int)Math.Ceiling(wait)))
                    return;
            }
        }

        /// <summary>when까지 기다린다. 그 전에 멈추라고 하면 true.</summary>
        private bool WaitUntil(DateTime when)
        {
            while (true)
            {
                var remaining = (when - DateTime.Now).TotalMilliseconds;
                if (remaining <= 0)
                    return false;
                if (remaining <= HighResolutionLeadMs)
                    UseHighResolution();
                if (_stop.WaitOne((int)Math.Min(Math.Ceiling(remaining), MaxWaitSliceMs)))
                    return true;
            }
        }

        /// <summary>타이머 해상도를 1ms로 올린다. 기본 15.6ms로는 1/100초 단위 간격이나 정확한 시작 시각을 지킬 수 없다.</summary>
        private void UseHighResolution()
        {
            if (_highResolution)
                return;
            NativeMethods.timeBeginPeriod(1);
            _highResolution = true;
        }

        private void NotifyFinished(string error)
        {
            try
            {
                _dispatcher.BeginInvoke(new Action(() => _finished(this, error)));
            }
            catch (Exception)
            {
                // Folderss가 끝나는 중이라 알릴 곳이 없다
            }
        }
    }
}
