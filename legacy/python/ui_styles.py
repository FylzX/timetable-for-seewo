"""Shared light, flat styling for the editing and clock windows."""

EDITOR_STYLE = """
QDialog { background: #f4f6fa; }
QWidget { color: #243047; font-family: 'Microsoft YaHei UI'; font-size: 13px; }
QLabel { background: transparent; }
QLabel#title { font-size: 24px; font-weight: 700; color: #17243b; }
QLabel#subtitle, QLabel#hint { color: #748199; }
QLabel#columnTitle { font-size: 16px; font-weight: 700; }
QLabel#eyebrow { font-size: 11px; font-weight: 700; color: #687991; }
QLabel#status { color: #52647e; }
QFrame#templatePanel, QFrame#dayPanel, QFrame#daysGroup {
    background: white; border: 1px solid #e1e6ef; border-radius: 12px;
}
QFrame#weekColumn { background: transparent; border: none; border-right: 1px solid #edf0f5; }
QFrame#lessonCard { background: #f4f7fc; border: 1px solid #e1e8f3; border-radius: 9px; }
QFrame#templateCard { background: #edf3ff; border: 1px solid #dce6fa; border-radius: 9px; }
QPushButton, QToolButton {
    background: white; border: 1px solid #dce3ed; border-radius: 7px;
    padding: 8px 12px; font-weight: 600;
}
QPushButton:hover, QToolButton:hover { background: #edf3ff; border-color: #a9bfe8; }
QPushButton:pressed { background: #dce8ff; }
QPushButton:disabled { color: #9ba7ba; background: #f1f3f7; border-color: #e6eaf0; }
QPushButton#primary { background: #3267e3; color: white; border: none; padding: 10px 24px; }
QPushButton#primary:hover { background: #2456ce; }
QPushButton#primary:disabled { background: #aebee4; }
QPushButton#danger, QToolButton#danger { color: #c34b59; }
QPushButton#timeButton { background: transparent; border: none; padding: 1px 0; text-align: left; color: #607594; font-size: 12px; }
QToolButton#deleteCard { background: transparent; border: none; padding: 0; color: #9aa7bc; font-size: 18px; }
QToolButton#deleteCard:hover { color: #ce4658; background: #fce9ed; }
QComboBox, QLineEdit, QDateEdit, QTimeEdit {
    background: white; border: 1px solid #dce3ed; border-radius: 6px; padding: 6px;
    min-height: 20px; selection-background-color: #3267e3;
}
QComboBox:focus, QLineEdit:focus, QDateEdit:focus, QTimeEdit:focus { border-color: #3267e3; }
QComboBox { padding-right: 20px; }
QComboBox::drop-down { border: none; width: 20px; }
QComboBox QAbstractItemView { background: white; selection-background-color: #e4edff; selection-color: #243047; }
QTabWidget::pane { border: none; background: transparent; }
QTabBar::tab { background: #e9edf5; color: #6a7890; padding: 11px 28px; border-radius: 7px; margin-right: 8px; font-weight: 600; }
QTabBar::tab:selected { background: #3267e3; color: white; }
QTabBar::tab:hover:!selected { background: #dde5f2; }
QScrollArea { border: none; background: transparent; }
QScrollArea > QWidget > QWidget { background: transparent; }
QScrollBar:vertical { width: 7px; background: transparent; margin: 3px; }
QScrollBar:horizontal { height: 9px; background: transparent; margin: 3px; }
QScrollBar::handle { background: #c4cedf; border-radius: 3px; min-height: 25px; min-width: 25px; }
QScrollBar::add-line, QScrollBar::sub-line { width: 0; height: 0; }
QScrollBar::add-page, QScrollBar::sub-page { background: transparent; }
QCalendarWidget QWidget { background: white; }
QCalendarWidget QToolButton { color: #243047; }
QMessageBox { background: #f4f6fa; }
"""
