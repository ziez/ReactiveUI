﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Linq;
using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text;

namespace ReactiveUI
{
    public static class ReactiveNotifyPropertyChangedMixin
    {
        /// <summary>
        /// ObservableForProperty returns an Observable representing the
        /// property change notifications for a specific property on a
        /// ReactiveObject. This method (unlike other Observables that return
        /// IObservedChange) guarantees that the Value property of
        /// the IObservedChange is set.
        /// </summary>
        /// <param name="property">An Expression representing the property (i.e.
        /// 'x => x.SomeProperty.SomeOtherProperty'</param>
        /// <param name="beforeChange">If True, the Observable will notify
        /// immediately before a property is going to change.</param>
        /// <returns>An Observable representing the property change
        /// notifications for the given property.</returns>
        public static IObservable<IObservedChange<TSender, TValue>> ObservableForProperty<TSender, TValue>(
                this TSender This,
                Expression<Func<TSender, TValue>> property,
                bool beforeChange = false)
            where TSender : INotifyPropertyChanged
        {
            var propertyNames = new LinkedList<string>(RxApp.expressionToPropertyNames(property));
            var subscriptions = new LinkedList<IDisposable>(propertyNames.Select(x => (IDisposable) null));
            var ret = new Subject<IObservedChange<TSender, TValue>>();

            /* x => x.Foo.Bar.Baz;
             * 
             * Subscribe to This, look for Foo
             * Subscribe to Foo, look for Bar
             * Subscribe to Bar, look for Baz
             * Subscribe to Baz, publish to Subject
             * Return Subject
             * 
             * If Bar changes (notification fires on Foo), resubscribe to new Bar
             * 	Resubscribe to new Baz, publish to Subject
             * 
             * If Baz changes (notification fires on Bar),
             * 	Resubscribe to new Baz, publish to Subject
             */

            subscribeToExpressionChain(
                This, 
                buildPropPathFromNodePtr(propertyNames.First),
                This, 
                propertyNames.First, 
                subscriptions.First, 
                beforeChange, 
                ret);

            return Observable.Create<IObservedChange<TSender, TValue>>(x => {
                var disp = ret.Subscribe(x.OnNext, x.OnError, x.OnCompleted);
                return () => {
                    subscriptions.ForEach(y => y.Dispose());
                    disp.Dispose();
                };
            });
        }

        static void subscribeToExpressionChain<TSender, TValue>(
                TSender origSource,
                string origPath,
                object source,
                LinkedListNode<string> propertyNames, 
                LinkedListNode<IDisposable> subscriptions, 
                bool beforeChange,
                Subject<IObservedChange<TSender, TValue>> subject
            )
        {
            var current = propertyNames;
            var currentSub = subscriptions;
            object currentObj = source;
            PropertyInfo pi = null;
            ObservedChange<TSender, TValue> obsCh;

            while(current.Next != null) {
                pi = RxApp.getPropertyInfoForProperty(currentObj.GetType(), current.Value);
                if (pi == null) {
                    subscriptions.List.Where(x => x != null).ForEach(x => x.Dispose());
                    throw new ArgumentException(String.Format("Property '{0}' does not exist in expression", current.Value));
                }

                var notifyObj = wrapInpcObjectIfNeeded(currentObj);
                if (notifyObj != null) {
                    var capture = new {current, currentObj, pi, currentSub};
                    var toDispose = new IDisposable[2];

                    var valGetter = new ObservedChange<object, TValue>() {
                        Sender = capture.currentObj,
                        PropertyName = buildPropPathFromNodePtr(capture.current),
                        Value = default(TValue),
                    };

                    TValue prevVal = default(TValue);
                    bool prevValSet = valGetter.TryGetValue(out prevVal);

                    toDispose[0] = notifyObj.Changing.Where(x => x.PropertyName == capture.current.Value).Subscribe(x => {
                        prevValSet = valGetter.TryGetValue(out prevVal);
                    });

                    toDispose[1] = notifyObj.Changed.Where(x => x.PropertyName == capture.current.Value).Subscribe(x => {
                        subscribeToExpressionChain(origSource, origPath, capture.pi.GetValue(capture.currentObj, null), capture.current.Next, capture.currentSub.Next, beforeChange, subject);

                        TValue newVal;
                        if (!valGetter.TryGetValue(out newVal)) {
                            return;
                        }
                        
                        if (prevValSet && EqualityComparer<TValue>.Default.Equals(prevVal, newVal)) {
                            return;
                        }

                        obsCh = new ObservedChange<TSender, TValue>() {
                            Sender = origSource,
                            PropertyName = origPath,
                            Value = default(TValue),
                        };

                        TValue obsChVal;
                        if (obsCh.TryGetValue(out obsChVal)) {
                            obsCh.Value = obsChVal;
                            subject.OnNext(obsCh);
                        }
                    });

                    currentSub.Value = Disposable.Create(() => { toDispose[0].Dispose(); toDispose[1].Dispose(); });
                }

                current = current.Next;
                currentSub = currentSub.Next;
                currentObj = pi.GetValue(currentObj, null);
            }

            var finalNotify = wrapInpcObjectIfNeeded(currentObj);
            if (currentSub.Value != null) {
                currentSub.Value.Dispose();
            }

            if (finalNotify == null) {
                return;
            }

            var propName = current.Value;
            pi = RxApp.getPropertyInfoForProperty(currentObj.GetType(), current.Value);

            currentSub.Value = (beforeChange ? finalNotify.Changing : finalNotify.Changed).Where(x => x.PropertyName == propName).Subscribe(x => {
                obsCh = new ObservedChange<TSender, TValue>() {
                    Sender = origSource,
                    PropertyName = origPath,
                    Value = (TValue)pi.GetValue(currentObj, null),
                };

                subject.OnNext(obsCh);
            });
        }

        static MemoizingMRUCache<INotifyPropertyChanged, IReactiveNotifyPropertyChanged> wrapperCache =
            new MemoizingMRUCache<INotifyPropertyChanged, IReactiveNotifyPropertyChanged>(
                (x, _) => new MakeObjectReactiveHelper(x),
                25);

        static IReactiveNotifyPropertyChanged wrapInpcObjectIfNeeded(object obj)
        {
            if (obj == null) {
                return null;
            }

            var ret = obj as IReactiveNotifyPropertyChanged;
            if (ret != null) {
                return ret;
            }

            var inpc = obj as INotifyPropertyChanged;
            if (inpc != null) {
                return wrapperCache.Get(inpc);
            }

            return null;
        }

        static string buildPropPathFromNodePtr(LinkedListNode<string> node)
        {
            var ret = new StringBuilder();
            var current = node;

            while(current.Next != null) {
                ret.Append(current.Value);
                ret.Append('.');
                current = current.Next;
            }

            ret.Append(current.Value);
            return ret.ToString();
        }


        /// <summary>
        /// ObservableForProperty returns an Observable representing the
        /// property change notifications for a specific property on a
        /// ReactiveObject, running the IObservedChange through a Selector
        /// function.
        /// </summary>
        /// <param name="property">An Expression representing the property (i.e.
        /// 'x => x.SomeProperty'</param>
        /// <param name="selector">A Select function that will be run on each
        /// item.</param>
        /// <param name="beforeChange">If True, the Observable will notify
        /// immediately before a property is going to change.</param>
        /// <returns>An Observable representing the property change
        /// notifications for the given property.</returns>
        public static IObservable<TRet> ObservableForProperty<TSender, TValue, TRet>(
                this TSender This, 
                Expression<Func<TSender, TValue>> property, 
                Func<TValue, TRet> selector, 
                bool beforeChange = false)
            where TSender : INotifyPropertyChanged
        {           
            Contract.Requires(selector != null);
            return This.ObservableForProperty(property, beforeChange).Select(x => selector(x.Value));
        }

        /* NOTE: This is left here for reference - the real one is expanded out 
         * to 10 parameters in VariadicTemplates.tt */
#if FALSE
        public static IObservable<TRet> WhenAny<TSender, T1, T2, TRet>(this TSender This, 
                Expression<Func<TSender, T1>> property1, 
                Expression<Func<TSender, T2>> property2,
                Func<IObservedChange<TSender, T1>, IObservedChange<TSender, T2>, TRet> selector)
            where TSender : IReactiveNotifyPropertyChanged
        {
            var slot1 = new ObservedChange<TSender, T1>() {
                Sender = This,
                PropertyName = String.Join(".", RxApp.expressionToPropertyNames(property1)),
            };
            T1 slot1Value = default(T1); slot1.TryGetValue(out slot1Value); slot1.Value = slot1Value;

            var slot2 = new ObservedChange<TSender, T2>() {
                Sender = This,
                PropertyName = String.Join(".", RxApp.expressionToPropertyNames(property2)),
            };
            T2 slot2Value = default(T2); slot2.TryGetValue(out slot2Value); slot2.Value = slot2Value;

            IObservedChange<TSender, T1> islot1 = slot1;
            IObservedChange<TSender, T2> islot2 = slot2;
            return Observable.CreateWithDisposable<TRet>(subject => {
                subject.OnNext(selector(slot1, slot2));

                return Observable.Merge(
                    This.ObservableForProperty(property1).Do(x => { lock (slot1) { islot1 = x.fillInValue(); } }).Select(x => selector(islot1, islot2)),
                    This.ObservableForProperty(property2).Do(x => { lock (slot2) { islot2 = x.fillInValue(); } }).Select(x => selector(islot1, islot2))
                ).Subscribe(subject);
            });
        }
#endif
    }
}

// vim: tw=120 ts=4 sw=4 et :
