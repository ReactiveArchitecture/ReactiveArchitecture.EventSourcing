﻿namespace Khala.EventSourcing.Sql
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Khala.FakeDomain;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Moq;
    using Ploeh.AutoFixture;
    using Ploeh.AutoFixture.AutoMoq;

    [TestClass]
    public class SqlEventSourcedRepository_specs
    {
        private IFixture _fixture;
        private ISqlEventStore _eventStore;
        private ISqlEventPublisher _eventPublisher;
        private IMementoStore _mementoStore;
        private SqlEventSourcedRepository<FakeUser> _sut;

        [TestInitialize]
        public void TestInitialize()
        {
            _fixture = new Fixture().Customize(new AutoMoqCustomization());
            _eventStore = Mock.Of<ISqlEventStore>();
            _eventPublisher = Mock.Of<ISqlEventPublisher>();
            _mementoStore = Mock.Of<IMementoStore>();
            _sut = new SqlEventSourcedRepository<FakeUser>(
                _eventStore,
                _eventPublisher,
                _mementoStore,
                FakeUser.Factory,
                FakeUser.Factory);
        }

        [TestMethod]
        public void sut_implements_IEventSourcedRepositoryT()
        {
            _sut.Should().BeAssignableTo
                <IEventSourcedRepository<FakeUser>>();
        }

        [TestMethod]
        public void sut_implements_ISqlEventSourcedRepository()
        {
            _sut.Should().BeAssignableTo
                <ISqlEventSourcedRepository<FakeUser>>();
        }

        [TestMethod]
        public async Task SaveAndPublish_saves_events()
        {
            FakeUser user = _fixture.Create<FakeUser>();
            string operationId = _fixture.Create<string>();
            var correlationId = Guid.NewGuid();
            string contributor = Guid.NewGuid().ToString();
            user.ChangeUsername(_fixture.Create("username"));
            var pendingEvents = new List<IDomainEvent>(user.PendingEvents);

            await _sut.SaveAndPublish(user, operationId, correlationId, contributor);

            Mock.Get(_eventStore).Verify(
                x =>
                x.SaveEvents<FakeUser>(
                    pendingEvents,
                    operationId,
                    correlationId,
                    contributor,
                    CancellationToken.None),
                Times.Once());
        }

        [TestMethod]
        public async Task SaveAndPublish_publishes_events()
        {
            FakeUser user = _fixture.Create<FakeUser>();
            string operationId = _fixture.Create<string>();
            var correlationId = Guid.NewGuid();
            string contributor = Guid.NewGuid().ToString();
            user.ChangeUsername(_fixture.Create("username"));

            await _sut.SaveAndPublish(user, operationId, correlationId, contributor);

            Mock.Get(_eventPublisher).Verify(
                x =>
                x.FlushPendingEvents(user.Id, CancellationToken.None),
                Times.Once());
        }

        [TestMethod]
        public void SaveAndPublish_does_not_publish_events_if_fails_to_save_events()
        {
            FakeUser user = _fixture.Create<FakeUser>();
            string operationId = _fixture.Create<string>();
            var correlationId = Guid.NewGuid();
            string contributor = _fixture.Create<string>();
            user.ChangeUsername(_fixture.Create("username"));
            Mock.Get(_eventStore)
                .Setup(
                    x =>
                    x.SaveEvents<FakeUser>(
                        It.IsAny<IEnumerable<IDomainEvent>>(),
                        operationId,
                        correlationId,
                        contributor,
                        CancellationToken.None))
                .Throws<InvalidOperationException>();

            Func<Task> action = () => _sut.SaveAndPublish(user, operationId, correlationId, contributor);

            action.ShouldThrow<InvalidOperationException>();
            Mock.Get(_eventPublisher).Verify(
                x =>
                x.FlushPendingEvents(user.Id, It.IsAny<CancellationToken>()),
                Times.Never());
        }

        [TestMethod]
        public async Task SaveAndPublish_saves_memento()
        {
            FakeUser user = _fixture.Create<FakeUser>();
            string operationId = _fixture.Create<string>();
            var correlationId = Guid.NewGuid();
            string contributor = _fixture.Create<string>();

            await _sut.SaveAndPublish(user, operationId, correlationId, contributor);

            Mock.Get(_mementoStore).Verify(
                x =>
                x.Save<FakeUser>(
                    user.Id,
                    It.Is<FakeUserMemento>(
                        p =>
                        p.Version == user.Version &&
                        p.Username == user.Username),
                    CancellationToken.None),
                Times.Once());
        }

        [TestMethod]
        public void SaveAndPublish_does_not_saves_memento_if_fails_to_save_events()
        {
            // Arrange
            FakeUser user = _fixture.Create<FakeUser>();
            string operationId = _fixture.Create<string>();
            var correlationId = Guid.NewGuid();
            string contributor = _fixture.Create<string>();
            Mock.Get(_eventStore)
                .Setup(
                    x =>
                    x.SaveEvents<FakeUser>(
                        It.IsAny<IEnumerable<IDomainEvent>>(),
                        operationId,
                        correlationId,
                        contributor,
                        CancellationToken.None))
                .Throws<InvalidOperationException>();

            // Act
            Func<Task> action = () => _sut.SaveAndPublish(user, operationId, correlationId, contributor);

            // Assert
            action.ShouldThrow<InvalidOperationException>();
            Mock.Get(_mementoStore).Verify(
                x =>
                x.Save<FakeUser>(
                    user.Id,
                    It.IsAny<IMemento>(),
                    It.IsAny<CancellationToken>()),
                Times.Never());
        }

        [TestMethod]
        public async Task Find_loads_events()
        {
            FakeUser user = _fixture.Create<FakeUser>();
            user.ChangeUsername(_fixture.Create("username"));
            Mock.Get(_eventStore)
                .Setup(
                    x =>
                    x.LoadEvents<FakeUser>(user.Id, 0, CancellationToken.None))
                .ReturnsAsync(user.FlushPendingEvents())
                .Verifiable();

            await _sut.Find(user.Id, CancellationToken.None);

            Mock.Get(_eventStore).Verify();
        }

        [TestMethod]
        public async Task Find_restores_aggregate_from_events()
        {
            // Arrange
            FakeUser user = _fixture.Create<FakeUser>();
            user.ChangeUsername(_fixture.Create("username"));

            Mock.Get(_eventStore)
                .Setup(
                    x =>
                    x.LoadEvents<FakeUser>(user.Id, 0, CancellationToken.None))
                .ReturnsAsync(user.FlushPendingEvents())
                .Verifiable();

            var sut = new SqlEventSourcedRepository<FakeUser>(
                _eventStore,
                _eventPublisher,
                FakeUser.Factory);

            // Act
            FakeUser actual = await sut.Find(user.Id, CancellationToken.None);

            // Assert
            Mock.Get(_eventStore).Verify();
            actual.ShouldBeEquivalentTo(user);
        }

        [TestMethod]
        public async Task Find_restores_aggregate_using_memento_if_found()
        {
            // Arrange
            FakeUser user = _fixture.Create<FakeUser>();
            IMemento memento = user.SaveToMemento();
            user.ChangeUsername(_fixture.Create("username"));

            Mock.Get(_mementoStore)
                .Setup(x => x.Find<FakeUser>(user.Id, CancellationToken.None))
                .ReturnsAsync(memento);

            Mock.Get(_eventStore)
                .Setup(
                    x =>
                    x.LoadEvents<FakeUser>(user.Id, 1, CancellationToken.None))
                .ReturnsAsync(user.FlushPendingEvents().Skip(1))
                .Verifiable();

            // Act
            FakeUser actual = await _sut.Find(user.Id, CancellationToken.None);

            // Assert
            Mock.Get(_eventStore).Verify();
            actual.ShouldBeEquivalentTo(user);
        }

        [TestMethod]
        public async Task Find_returns_null_if_no_event()
        {
            var userId = Guid.NewGuid();
            Mock.Get(_eventStore)
                .Setup(
                    x =>
                    x.LoadEvents<FakeUser>(userId, 0, CancellationToken.None))
                .ReturnsAsync(new IDomainEvent[0]);

            FakeUser actual = await _sut.Find(userId, CancellationToken.None);

            actual.Should().BeNull();
        }

        [TestMethod]
        public async Task FindIdByUniqueIndexedProperty_relays_to_event_store()
        {
            // Arrange
            string name = _fixture.Create<string>();
            string value = _fixture.Create<string>();
            Guid? expected = _fixture.Create<Guid?>();

            ISqlEventStore eventStore = Mock.Of<ISqlEventStore>();
            Mock.Get(eventStore)
                .Setup(
                    x =>
                    x.FindIdByUniqueIndexedProperty<FakeUser>(
                        name,
                        value,
                        CancellationToken.None))
                .ReturnsAsync(expected);

            var sut = new SqlEventSourcedRepository<FakeUser>(
                eventStore,
                Mock.Of<ISqlEventPublisher>(),
                Mock.Of<Func<Guid, IEnumerable<IDomainEvent>, FakeUser>>());

            // Act
            Guid? actual = await sut.FindIdByUniqueIndexedProperty(name, value, CancellationToken.None);

            // Assert
            actual.Should().Be(expected);
        }
    }
}
