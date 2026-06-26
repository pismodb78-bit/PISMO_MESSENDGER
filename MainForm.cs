private void btnSend_MouseEnter(object sender, EventArgs e)
{
    btnSend.BackColor = Color.FromArgb(71, 82, 196);
}

private void btnSend_MouseLeave(object sender, EventArgs e)
{
    btnSend.BackColor = Color.FromArgb(88, 101, 242);
}

private void pnlInputBar_Resize(object sender, EventArgs e)
{
    txtMessage.Width = pnlInputBar.Width - 146 - 154 - 12;
    btnSend.Location = new Point(pnlInputBar.Width - 152, 14);
}