namespace MySqlConnector.Utilities;

#pragma warning disable CA1001 // this is a Singleton, so doesn't need to be IDisposable
internal sealed class TimerQueue
#pragma warning restore CA1001
{
	public static TimerQueue Instance { get; } = new();

	/// <summary>
	/// Adds a timer that will invoke <paramref name="action"/> in approximately <paramref name="delay"/> milliseconds.
	/// </summary>
	/// <param name="delay">The time (in milliseconds) to wait before invoking <paramref name="action"/>.</param>
	/// <param name="action">The callback to invoke.</param>
	/// <returns>A timer ID that can be passed to <see cref="Remove"/> to cancel the timer.</returns>
	public uint Add(int delay, Action action)
	{
		if (delay < 0)
			throw new ArgumentOutOfRangeException(nameof(delay), $"delay must not be negative: {delay}");

		var current = Environment.TickCount;
		lock (m_lock)
		{
			var id = unchecked(++m_counter);
			if (id == 0)
				id = ++m_counter;

			var absolute = unchecked(current + delay);
			InsertIntoQueue(new(id, absolute, absolute, action));
			return id;
		}
	}

	/// <summary>
	/// Cancels the timer with the specified ID.
	/// </summary>
	/// <param name="id">The timer ID (returned from <see cref="Add"/>).</param>
	/// <returns><c>true</c> if the timer was removed; otherwise, <c>false</c>. This method will return <c>false</c> if the specified timer has already fired.</returns>
	public bool Remove(uint id)
	{
		lock (m_lock)
		{
			for (var i = 0; i < m_timeoutActions.Count; i++)
			{
				if (m_timeoutActions[i].Id == id)
				{
					m_timeoutActions.RemoveAt(i);
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>
	/// Resets the delay of the timer with the specified ID.
	/// </summary>
	/// <param name="id">The timer ID (returned from <see cref="Add"/>).</param>
	/// <param name="delay">The new time (in milliseconds) to wait before invoking the callback.</param>
	public void ResetDelay(uint id, int delay)
	{
		if (delay < 0)
			throw new ArgumentOutOfRangeException(nameof(delay), $"delay must not be negative: {delay}");

		var current = Environment.TickCount;
		lock (m_lock)
		{
			for (var i = 0; i < m_timeoutActions.Count; i++)
			{
				if (m_timeoutActions[i].Id == id)
				{
					var absolute = unchecked(current + delay);
					var existing = m_timeoutActions[i];

					// if we are postponing the fire time, just update the existing entry
					if (absolute > existing.InitialTime)
					{
						m_timeoutActions[i] = new(id, existing.InitialTime, absolute, existing.Action);
					}
					// if we are bringing the fire time forward, remove the existing entry and re-insert it
					else
					{
						m_timeoutActions.RemoveAt(i);
						InsertIntoQueue(new(id, absolute, absolute, existing.Action));
					}
					return;
				}
			}
		}
	}

	private TimerQueue()
	{
		m_lock = new();
		m_timer = new(Callback, this, -1, -1);
		m_timeoutActions = new();
	}

	private void Callback(object? obj)
	{
		const int tickResolution = 15;
		var toBeCalled = new List<Action>();

		lock (m_lock)
		{
			// process all timers that have expired or will expire in the granularity of a clock tick
			while (m_timeoutActions.Count > 0 && unchecked(m_timeoutActions[0].InitialTime - Environment.TickCount) < tickResolution)
			{
				var data = m_timeoutActions[0];
				m_timeoutActions.RemoveAt(0);
				if (data.NewTime > data.InitialTime)
				{
					InsertIntoQueue(new(data.Id, data.NewTime, data.NewTime, data.Action));
				}
				else
				{
					toBeCalled.Add(data.Action);
				}
			}

			if (m_timeoutActions.Count == 0)
			{
				UnsafeClearTimer();
			}
			else
			{
				var delay = Math.Max(250, unchecked(m_timeoutActions[0].InitialTime - Environment.TickCount));
				UnsafeSetTimer(delay);
			}
		}

		foreach (var action in toBeCalled)
		{
			action();
		}
	}

	// Should be called while holding m_lock.
	private void InsertIntoQueue(Data data)
	{
		// insert this callback in the list ascending tick-count order
		var index = m_timeoutActions.Count;
		while (index > 0 && data.InitialTime < m_timeoutActions[index - 1].InitialTime)
			index--;
		m_timeoutActions.Insert(index, data);

		if (!m_isTimerEnabled || (index == 0 && m_nextTimerTick > data.InitialTime))
			UnsafeSetTimer(unchecked(data.InitialTime - Environment.TickCount));
	}

	// Should be called while holding m_lock.
	private void UnsafeSetTimer(int delay)
	{
		m_nextTimerTick = unchecked(Environment.TickCount + delay);
		m_isTimerEnabled = true;
		m_timer.Change(delay, -1);
	}

	// Should be called while holding m_lock.
	private void UnsafeClearTimer()
	{
		m_nextTimerTick = 0;
		m_isTimerEnabled = false;
		m_timer.Change(-1, -1);
	}

	private readonly struct Data
	{
		public Data(uint id, int initialTime, int newTime, Action action)
		{
			Id = id;
			InitialTime = initialTime;
			NewTime = newTime;
			Action = action;
		}

		public uint Id { get; }
		public int InitialTime { get; }
		public int NewTime { get; }
		public Action Action { get; }
	}

	private readonly object m_lock;
	private readonly Timer m_timer;
	private readonly List<Data> m_timeoutActions;
	private uint m_counter;
	private bool m_isTimerEnabled;
	private int m_nextTimerTick;
}
