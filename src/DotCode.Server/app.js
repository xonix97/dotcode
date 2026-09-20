document.addEventListener('DOMContentLoaded', () => {
  const STORAGE_KEY = 'click_count';
  const counterBtn = document.getElementById('counter-btn');
  const countDisplay = document.getElementById('count-display');

  function getStoredCount() {
    const raw = localStorage.getItem(STORAGE_KEY);
    const parsed = parseInt(raw, 10);
    return isNaN(parsed) ? 0 : parsed;
  }

  function updateDisplay(value) {
    if (countDisplay) {
      countDisplay.textContent = value;
    }
  }

  let count = getStoredCount();
  updateDisplay(count);

  if (counterBtn) {
    counterBtn.addEventListener('click', () => {
      count += 1;
      localStorage.setItem(STORAGE_KEY, count.toString());
      updateDisplay(count);
    });
  }
});
