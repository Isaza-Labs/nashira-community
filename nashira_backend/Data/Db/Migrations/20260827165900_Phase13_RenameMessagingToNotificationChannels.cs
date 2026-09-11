using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <summary>
    /// Renames the outbound notification tables out of the "messaging" namespace so
    /// that name is free for the bidirectional chat channels (Slack / Telegram /
    /// WhatsApp / Teams), which are a different feature entirely.
    ///
    /// Scaffolded by EF as drop + create; rewritten by hand as renames. These tables
    /// hold live channel configuration — including the encrypted webhook URL, which
    /// cannot be recovered once dropped because the API never returns it — plus the
    /// delivery history that answers "did the on-call channel actually get the
    /// alert?". A drop/create would silently destroy both.
    /// </summary>
    public partial class Phase13_RenameMessagingToNotificationChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "messaging_channels",
                newName: "notification_channels");

            migrationBuilder.RenameTable(
                name: "messaging_deliveries",
                newName: "notification_deliveries");

            migrationBuilder.RenameColumn(
                name: "MessagingChannelId",
                table: "notification_channels",
                newName: "NotificationChannelId");

            migrationBuilder.RenameColumn(
                name: "MessagingDeliveryId",
                table: "notification_deliveries",
                newName: "NotificationDeliveryId");

            migrationBuilder.RenameColumn(
                name: "MessagingChannelId",
                table: "notification_deliveries",
                newName: "NotificationChannelId");

            migrationBuilder.RenameIndex(
                name: "IX_messaging_channels_Name",
                table: "notification_channels",
                newName: "IX_notification_channels_Name");

            migrationBuilder.RenameIndex(
                name: "IX_messaging_channels_Slug",
                table: "notification_channels",
                newName: "IX_notification_channels_Slug");

            migrationBuilder.RenameIndex(
                name: "IX_messaging_deliveries_MessagingChannelId_SentAt",
                table: "notification_deliveries",
                newName: "IX_notification_deliveries_NotificationChannelId_SentAt");

            // A renamed table keeps its old primary-key constraint name. Rename it
            // too, so the schema matches what the model expects and the next scaffold
            // does not try to "fix" it.
            migrationBuilder.Sql(
                @"ALTER TABLE notification_channels RENAME CONSTRAINT ""PK_messaging_channels"" TO ""PK_notification_channels"";");
            migrationBuilder.Sql(
                @"ALTER TABLE notification_deliveries RENAME CONSTRAINT ""PK_messaging_deliveries"" TO ""PK_notification_deliveries"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE notification_deliveries RENAME CONSTRAINT ""PK_notification_deliveries"" TO ""PK_messaging_deliveries"";");
            migrationBuilder.Sql(
                @"ALTER TABLE notification_channels RENAME CONSTRAINT ""PK_notification_channels"" TO ""PK_messaging_channels"";");

            migrationBuilder.RenameIndex(
                name: "IX_notification_deliveries_NotificationChannelId_SentAt",
                table: "notification_deliveries",
                newName: "IX_messaging_deliveries_MessagingChannelId_SentAt");

            migrationBuilder.RenameIndex(
                name: "IX_notification_channels_Slug",
                table: "notification_channels",
                newName: "IX_messaging_channels_Slug");

            migrationBuilder.RenameIndex(
                name: "IX_notification_channels_Name",
                table: "notification_channels",
                newName: "IX_messaging_channels_Name");

            migrationBuilder.RenameColumn(
                name: "NotificationChannelId",
                table: "notification_deliveries",
                newName: "MessagingChannelId");

            migrationBuilder.RenameColumn(
                name: "NotificationDeliveryId",
                table: "notification_deliveries",
                newName: "MessagingDeliveryId");

            migrationBuilder.RenameColumn(
                name: "NotificationChannelId",
                table: "notification_channels",
                newName: "MessagingChannelId");

            migrationBuilder.RenameTable(
                name: "notification_deliveries",
                newName: "messaging_deliveries");

            migrationBuilder.RenameTable(
                name: "notification_channels",
                newName: "messaging_channels");
        }
    }
}
